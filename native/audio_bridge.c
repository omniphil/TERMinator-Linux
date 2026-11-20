/*
 * audio_bridge.c — OpenAL Soft audio backend for TERMinator Linux.
 *
 * 7 OpenAL sources (channels 0-3 for TAP+, channel 4 for bell, channel 5 for the About box music, channel 6 for
 * SyncTERM door audio).
 * Streaming playback via alSourceQueueBuffers ring buffer.
 * File decoding (MP3/WAV/OGG) runs on per-channel background threads.
 * PCM ring buffer per channel for analyzer snapshots.
 */

#include "audio_bridge.h"

#include <AL/al.h>
#include <AL/alc.h>
#include <pthread.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <unistd.h>

/* ---------- Header-only decoders ---------- */
#define MINIMP3_IMPLEMENTATION
#include "minimp3.h"

#define DR_WAV_IMPLEMENTATION
#include "dr_wav.h"

#define STB_VORBIS_HEADER_ONLY
#include "stb_vorbis.c"

/* ---------- Constants ---------- */
#define NUM_CHANNELS       7   /* 0-3 TAP+, 4 bell, 5 About music, 6 SyncTERM door audio */
#define STREAM_BUFS        4        /* ring-buffer depth per streaming channel */
#define STREAM_BUF_FRAMES  4096     /* frames per streaming buffer */
#define SNAPSHOT_FRAMES    2048     /* PCM ring buffer for analyzer */

/* ---------- Per-channel state ---------- */
typedef struct {
    ALuint       source;
    ALuint       streamBufs[STREAM_BUFS];

    /* File-decode thread */
    pthread_t    decodeThread;
    int          decodeThreadRunning;
    int          stopRequested;
    int          looping;
    char        *filepath;

    /* Volume */
    float        volume;

    /* PCM snapshot ring for analyzer */
    short        snapshot[SNAPSHOT_FRAMES * 2];  /* stereo interleaved */
    int          snapshotWritePos;

    /* Paused flag */
    int          paused;

    /* Queued PCM push (audio_bridge_queue_pcm): stream buffers not currently on the source's queue */
    ALuint       pcmFree[STREAM_BUFS];
    int          pcmFreeCount;
    int          pcmQueueActive;
} AudioChannel;

/* ---------- Globals ---------- */
static ALCdevice  *g_device  = NULL;
static ALCcontext *g_context = NULL;
static AudioChannel g_channels[NUM_CHANNELS];
static pthread_mutex_t g_mutex = PTHREAD_MUTEX_INITIALIZER;
static int g_initialized = 0;

/* Short-clip pool (key clicks): one buffer per uploaded clip, a few sources shared by all of them */
#define NUM_CLIPS        16
#define CLIP_POOL_SIZE   8
static ALuint g_clipBufs[NUM_CLIPS];
static int    g_clipLoaded[NUM_CLIPS];
static ALuint g_clipSources[CLIP_POOL_SIZE];
static int    g_clipPoolReady = 0;
static int    g_clipNext = 0;    /* where the search for a free source starts (round robin) */

/* ---------- Forward declarations ---------- */
static void *decode_thread_func(void *arg);
static void  channel_stop_internal(int ch);
static void  feed_snapshot(int ch, const short *samples, int frames, int numCh);

/* ================================================================
 *  Lifecycle
 * ================================================================ */

/*
 * The OpenAL device is opened lazily, on the first sound actually played (a bell, a key click, the About music, a
 * TAP+ tune, door/TRACE audio), never at launch. An open OpenAL Soft device streams silence to PulseAudio for as
 * long as it is open, and under WSLg merely opening that stream at startup made an audible sound on the speakers.
 * audio_bridge_init only checks that an output device exists (enumeration, no stream), so the app can still tell
 * doors whether it has audio. Windows plays nothing at launch either.
 */
static int g_open = 0;              /* device + context + sources exist */
static int g_openFailed = 0;        /* the lazy open failed once: don't retry on every chunk */

/* Clips uploaded before the device is open (key-click WarmUp at launch) wait here until it is */
typedef struct { short *samples; int frames, sampleRate, numChannels; } PendingClip;
static PendingClip g_clipPending[NUM_CLIPS];

static int  upload_clip_locked(int clipId, const short *samples, int frames, int sampleRate, int numChannels);

int audio_bridge_init(void)
{
    pthread_mutex_lock(&g_mutex);
    if (g_initialized) {
        pthread_mutex_unlock(&g_mutex);
        return 1;
    }

    /* Is there an output device at all? (Enumeration only: no stream is opened, nothing reaches the speakers.) */
    const ALCchar *def = alcGetString(NULL, ALC_DEFAULT_DEVICE_SPECIFIER);
    if (!def) {
        fprintf(stderr, "audio_bridge: no OpenAL output device\n");
        pthread_mutex_unlock(&g_mutex);
        return 0;
    }

    for (int i = 0; i < NUM_CHANNELS; i++) {
        memset(&g_channels[i], 0, sizeof(AudioChannel));
        g_channels[i].volume = 1.0f;
    }

    g_initialized = 1;
    pthread_mutex_unlock(&g_mutex);
    return 1;
}

/* Opens the device on first use. Must be called with g_mutex held. Returns 1 once it's open. */
static int ensure_open_locked(void)
{
    if (!g_initialized) return 0;
    if (g_open) return 1;
    if (g_openFailed) return 0;

    g_device = alcOpenDevice(NULL);
    if (!g_device) {
        fprintf(stderr, "audio_bridge: alcOpenDevice failed\n");
        g_openFailed = 1;
        return 0;
    }

    g_context = alcCreateContext(g_device, NULL);
    if (!g_context) {
        fprintf(stderr, "audio_bridge: alcCreateContext failed\n");
        alcCloseDevice(g_device);
        g_device = NULL;
        g_openFailed = 1;
        return 0;
    }
    alcMakeContextCurrent(g_context);

    for (int i = 0; i < NUM_CHANNELS; i++) {
        alGenSources(1, &g_channels[i].source);
        alGenBuffers(STREAM_BUFS, g_channels[i].streamBufs);
        alSourcef(g_channels[i].source, AL_GAIN, g_channels[i].volume);  /* any volume set before the open */
    }
    g_open = 1;

    for (int i = 0; i < NUM_CLIPS; i++) {
        PendingClip *pc = &g_clipPending[i];
        if (pc->samples) {
            upload_clip_locked(i, pc->samples, pc->frames, pc->sampleRate, pc->numChannels);
            free(pc->samples);
            pc->samples = NULL;
        }
    }
    return 1;
}

void audio_bridge_shutdown(void)
{
    pthread_mutex_lock(&g_mutex);
    if (!g_initialized) {
        pthread_mutex_unlock(&g_mutex);
        return;
    }

    for (int i = 0; i < NUM_CLIPS; i++) {
        free(g_clipPending[i].samples);
        g_clipPending[i].samples = NULL;
    }

    if (g_open) {
        for (int i = 0; i < NUM_CHANNELS; i++) {
            channel_stop_internal(i);
            alDeleteSources(1, &g_channels[i].source);
            alDeleteBuffers(STREAM_BUFS, g_channels[i].streamBufs);
        }

        /* Clip pool: sources first, so no buffer is still attached when it's deleted */
        if (g_clipPoolReady) {
            alSourceStopv(CLIP_POOL_SIZE, g_clipSources);
            alDeleteSources(CLIP_POOL_SIZE, g_clipSources);
            g_clipPoolReady = 0;
        }
        for (int i = 0; i < NUM_CLIPS; i++) {
            if (g_clipLoaded[i]) {
                alDeleteBuffers(1, &g_clipBufs[i]);
                g_clipLoaded[i] = 0;
            }
        }

        alcMakeContextCurrent(NULL);
        if (g_context) { alcDestroyContext(g_context); g_context = NULL; }
        if (g_device)  { alcCloseDevice(g_device);     g_device  = NULL; }
        g_open = 0;
    }

    g_initialized = 0;
    pthread_mutex_unlock(&g_mutex);
}

/* ================================================================
 *  PCM playback (bells, MOD render push)
 * ================================================================ */

int audio_bridge_play_pcm(int channel, const short *samples, int frames,
                          int sampleRate, int numChannels)
{
    if (!samples || frames <= 0 || channel < 0 || channel >= NUM_CHANNELS)
        return 0;

    pthread_mutex_lock(&g_mutex);
    if (!ensure_open_locked()) { pthread_mutex_unlock(&g_mutex); return 0; }

    AudioChannel *ch = &g_channels[channel];

    /* Stop any running decode thread / queued buffers first */
    channel_stop_internal(channel);

    ALenum format = (numChannels >= 2) ? AL_FORMAT_STEREO16 : AL_FORMAT_MONO16;
    int byteSize = frames * numChannels * (int)sizeof(short);

    /* Use the first stream buffer as a one-shot buffer */
    alBufferData(ch->streamBufs[0], format, samples, byteSize, sampleRate);
    alSourcei(ch->source, AL_BUFFER, ch->streamBufs[0]);
    alSourcei(ch->source, AL_LOOPING, AL_FALSE);
    alSourcef(ch->source, AL_GAIN, ch->volume);
    alSourcePlay(ch->source);

    /* Feed analyzer snapshot */
    feed_snapshot(channel, samples, frames, numChannels);

    pthread_mutex_unlock(&g_mutex);
    return 1;
}

/* ================================================================
 *  Queued PCM push (gapless render loop, e.g. the About box music)
 * ================================================================ */

int audio_bridge_queue_pcm(int channel, const short *samples, int frames,
                           int sampleRate, int numChannels)
{
    if (!samples || frames <= 0 || channel < 0 || channel >= NUM_CHANNELS)
        return 0;

    pthread_mutex_lock(&g_mutex);
    if (!ensure_open_locked()) { pthread_mutex_unlock(&g_mutex); return 0; }

    AudioChannel *ch = &g_channels[channel];

    /* First push since a stop / play_pcm / play_file: clear the source, then all stream buffers are free */
    if (!ch->pcmQueueActive) {
        channel_stop_internal(channel);
        for (int i = 0; i < STREAM_BUFS; i++)
            ch->pcmFree[i] = ch->streamBufs[i];
        ch->pcmFreeCount = STREAM_BUFS;
        ch->pcmQueueActive = 1;
    }

    /* Buffers that have played out come back to the free list */
    ALint processed = 0;
    alGetSourcei(ch->source, AL_BUFFERS_PROCESSED, &processed);
    while (processed-- > 0) {
        ALuint done;
        alSourceUnqueueBuffers(ch->source, 1, &done);
        if (ch->pcmFreeCount < STREAM_BUFS)
            ch->pcmFree[ch->pcmFreeCount++] = done;
    }

    /* Queue full: the caller waits for some of it to play and tries again */
    if (ch->pcmFreeCount == 0) {
        pthread_mutex_unlock(&g_mutex);
        return 0;
    }

    ALuint buf = ch->pcmFree[--ch->pcmFreeCount];
    ALenum format = (numChannels >= 2) ? AL_FORMAT_STEREO16 : AL_FORMAT_MONO16;
    int byteSize = frames * (numChannels >= 2 ? 2 : 1) * (int)sizeof(short);
    alBufferData(buf, format, samples, byteSize, sampleRate);
    alSourceQueueBuffers(ch->source, 1, &buf);

    /* Start it, or restart it after an underrun (a source stops once its queue runs dry) */
    ALint state = 0;
    alGetSourcei(ch->source, AL_SOURCE_STATE, &state);
    if (state != AL_PLAYING && !ch->paused) {
        alSourcef(ch->source, AL_GAIN, ch->volume);
        alSourcePlay(ch->source);
    }

    feed_snapshot(channel, samples, frames, numChannels);

    pthread_mutex_unlock(&g_mutex);
    return 1;
}

/* ================================================================
 *  Short overlapping clips (key clicks)
 * ================================================================ */

int audio_bridge_load_clip(int clipId, const short *samples, int frames,
                           int sampleRate, int numChannels)
{
    if (!samples || frames <= 0 || sampleRate <= 0 || clipId < 0 || clipId >= NUM_CLIPS)
        return 0;

    pthread_mutex_lock(&g_mutex);
    if (!g_initialized || g_openFailed) { pthread_mutex_unlock(&g_mutex); return 0; }

    int ok;
    if (g_open) {
        ok = upload_clip_locked(clipId, samples, frames, sampleRate, numChannels);
    } else {
        /* Not open yet (WarmUp at launch): keep a copy for the first play -- loading must not open the device */
        size_t bytes = (size_t)frames * (numChannels >= 2 ? 2 : 1) * sizeof(short);
        short *copy = (short *)malloc(bytes);
        ok = copy != NULL;
        if (ok) {
            memcpy(copy, samples, bytes);
            PendingClip *pc = &g_clipPending[clipId];
            free(pc->samples);
            pc->samples = copy;
            pc->frames = frames;
            pc->sampleRate = sampleRate;
            pc->numChannels = numChannels;
        }
    }

    pthread_mutex_unlock(&g_mutex);
    return ok;
}

/* Must be called with g_mutex held and the device open */
static int upload_clip_locked(int clipId, const short *samples, int frames, int sampleRate, int numChannels)
{
    if (!g_clipPoolReady) {
        alGenSources(CLIP_POOL_SIZE, g_clipSources);
        for (int i = 0; i < CLIP_POOL_SIZE; i++) {
            alSourcei(g_clipSources[i], AL_LOOPING, AL_FALSE);
            alSourcei(g_clipSources[i], AL_SOURCE_RELATIVE, AL_TRUE);  /* centred on the listener, never attenuated */
        }
        g_clipPoolReady = 1;
    }

    if (!g_clipLoaded[clipId]) {
        alGenBuffers(1, &g_clipBufs[clipId]);
        g_clipLoaded[clipId] = 1;
    } else {
        /* Reloading: a buffer can't be refilled while a source is still holding it */
        for (int i = 0; i < CLIP_POOL_SIZE; i++) {
            ALint attached = 0;
            alGetSourcei(g_clipSources[i], AL_BUFFER, &attached);
            if ((ALuint)attached == g_clipBufs[clipId]) {
                alSourceStop(g_clipSources[i]);
                alSourcei(g_clipSources[i], AL_BUFFER, 0);
            }
        }
    }

    ALenum format = (numChannels >= 2) ? AL_FORMAT_STEREO16 : AL_FORMAT_MONO16;
    int byteSize = frames * (numChannels >= 2 ? 2 : 1) * (int)sizeof(short);
    alBufferData(g_clipBufs[clipId], format, samples, byteSize, sampleRate);
    return 1;
}

int audio_bridge_play_clip(int clipId, float gain)
{
    if (clipId < 0 || clipId >= NUM_CLIPS)
        return 0;

    pthread_mutex_lock(&g_mutex);
    if (!ensure_open_locked() || !g_clipPoolReady || !g_clipLoaded[clipId]) {
        pthread_mutex_unlock(&g_mutex);
        return 0;
    }

    /* A source that's free, or else the next one round (cutting the oldest click short) */
    int pick = -1;
    for (int i = 0; i < CLIP_POOL_SIZE; i++) {
        int idx = (g_clipNext + i) % CLIP_POOL_SIZE;
        ALint state = 0;
        alGetSourcei(g_clipSources[idx], AL_SOURCE_STATE, &state);
        if (state != AL_PLAYING) {
            pick = idx;
            break;
        }
    }
    if (pick < 0)
        pick = g_clipNext;
    g_clipNext = (pick + 1) % CLIP_POOL_SIZE;

    ALuint src = g_clipSources[pick];
    alSourceStop(src);
    alSourcei(src, AL_BUFFER, (ALint)g_clipBufs[clipId]);
    alSourcef(src, AL_GAIN, gain < 0.0f ? 0.0f : (gain > 1.0f ? 1.0f : gain));
    alSourcePlay(src);

    pthread_mutex_unlock(&g_mutex);
    return 1;
}

/* ================================================================
 *  File playback (background decode thread)
 * ================================================================ */

int audio_bridge_play_file(int channel, const char *filepath, int loop)
{
    if (!filepath || channel < 0 || channel >= NUM_CHANNELS)
        return 0;

    pthread_mutex_lock(&g_mutex);
    if (!ensure_open_locked()) { pthread_mutex_unlock(&g_mutex); return 0; }

    AudioChannel *ch = &g_channels[channel];
    channel_stop_internal(channel);

    ch->filepath = strdup(filepath);
    ch->looping = loop;
    ch->stopRequested = 0;
    ch->paused = 0;
    ch->decodeThreadRunning = 1;

    pthread_create(&ch->decodeThread, NULL, decode_thread_func,
                   (void *)(long)channel);

    pthread_mutex_unlock(&g_mutex);
    return 1;
}

/* ================================================================
 *  Transport controls
 * ================================================================ */

void audio_bridge_stop(int channel)
{
    if (channel < 0 || channel >= NUM_CHANNELS) return;
    pthread_mutex_lock(&g_mutex);
    if (g_open) channel_stop_internal(channel);
    pthread_mutex_unlock(&g_mutex);
}

void audio_bridge_stop_all(void)
{
    pthread_mutex_lock(&g_mutex);
    if (g_open) {
        for (int i = 0; i < NUM_CHANNELS; i++)
            channel_stop_internal(i);
    }
    pthread_mutex_unlock(&g_mutex);
}

void audio_bridge_pause(int channel)
{
    if (channel < 0 || channel >= NUM_CHANNELS) return;
    pthread_mutex_lock(&g_mutex);
    if (g_open) {
        alSourcePause(g_channels[channel].source);
        g_channels[channel].paused = 1;
    }
    pthread_mutex_unlock(&g_mutex);
}

void audio_bridge_resume(int channel)
{
    if (channel < 0 || channel >= NUM_CHANNELS) return;
    pthread_mutex_lock(&g_mutex);
    if (g_open && g_channels[channel].paused) {
        alSourcePlay(g_channels[channel].source);
        g_channels[channel].paused = 0;
    }
    pthread_mutex_unlock(&g_mutex);
}

void audio_bridge_set_volume(int channel, float volume)
{
    if (channel < 0 || channel >= NUM_CHANNELS) return;
    if (volume < 0.0f) volume = 0.0f;
    if (volume > 1.0f) volume = 1.0f;

    pthread_mutex_lock(&g_mutex);
    if (g_initialized) {
        g_channels[channel].volume = volume;   /* kept for the lazy open if the device isn't open yet */
        if (g_open)
            alSourcef(g_channels[channel].source, AL_GAIN, volume);
    }
    pthread_mutex_unlock(&g_mutex);
}

void audio_bridge_seek(int channel, int positionMs)
{
    /* Seek is only meaningful for file playback.
     * For simplicity, we restart playback from the given position.
     * A full implementation would need per-format seek support. */
    (void)channel;
    (void)positionMs;
    /* TODO: implement per-format seek if needed */
}

int audio_bridge_is_playing(int channel)
{
    if (channel < 0 || channel >= NUM_CHANNELS) return 0;

    pthread_mutex_lock(&g_mutex);
    if (!g_open) { pthread_mutex_unlock(&g_mutex); return 0; }

    ALint state = 0;
    alGetSourcei(g_channels[channel].source, AL_SOURCE_STATE, &state);
    int playing = (state == AL_PLAYING) || g_channels[channel].decodeThreadRunning;

    pthread_mutex_unlock(&g_mutex);
    return playing;
}

/* ================================================================
 *  Analyzer PCM snapshot
 * ================================================================ */

int audio_bridge_get_pcm_snapshot(int channel, short *buffer, int maxFrames)
{
    if (!buffer || maxFrames <= 0 || channel < 0 || channel >= NUM_CHANNELS)
        return 0;

    pthread_mutex_lock(&g_mutex);
    if (!g_initialized) { pthread_mutex_unlock(&g_mutex); return 0; }

    AudioChannel *ch = &g_channels[channel];
    int available = SNAPSHOT_FRAMES;
    int framesToCopy = (maxFrames < available) ? maxFrames : available;

    /* Copy from ring buffer starting from oldest data */
    int readPos = ch->snapshotWritePos;
    for (int i = 0; i < framesToCopy; i++) {
        int idx = (readPos + i) % SNAPSHOT_FRAMES;
        buffer[i * 2]     = ch->snapshot[idx * 2];
        buffer[i * 2 + 1] = ch->snapshot[idx * 2 + 1];
    }

    pthread_mutex_unlock(&g_mutex);
    return framesToCopy;
}

/* ================================================================
 *  Internal helpers
 * ================================================================ */

static void channel_stop_internal(int ch)
{
    /* Must be called with g_mutex held */
    AudioChannel *c = &g_channels[ch];

    c->stopRequested = 1;

    /* Wait for decode thread to finish */
    if (c->decodeThreadRunning) {
        pthread_mutex_unlock(&g_mutex);
        pthread_join(c->decodeThread, NULL);
        pthread_mutex_lock(&g_mutex);
        c->decodeThreadRunning = 0;
    }

    alSourceStop(c->source);

    /* Unqueue all buffers */
    ALint queued = 0;
    alGetSourcei(c->source, AL_BUFFERS_QUEUED, &queued);
    while (queued-- > 0) {
        ALuint buf;
        alSourceUnqueueBuffers(c->source, 1, &buf);
    }

    /* Detach any direct buffer */
    alSourcei(c->source, AL_BUFFER, 0);

    if (c->filepath) {
        free(c->filepath);
        c->filepath = NULL;
    }
    c->paused = 0;
    c->stopRequested = 0;
    c->pcmQueueActive = 0;   /* every buffer is off the source now */
}

static void feed_snapshot(int ch, const short *samples, int frames, int numCh)
{
    /* Must be called with g_mutex held */
    AudioChannel *c = &g_channels[ch];

    for (int i = 0; i < frames; i++) {
        int wp = c->snapshotWritePos;
        if (numCh >= 2) {
            c->snapshot[wp * 2]     = samples[i * numCh];
            c->snapshot[wp * 2 + 1] = samples[i * numCh + 1];
        } else {
            c->snapshot[wp * 2]     = samples[i];
            c->snapshot[wp * 2 + 1] = samples[i];
        }
        c->snapshotWritePos = (wp + 1) % SNAPSHOT_FRAMES;
    }
}

/* ================================================================
 *  Background decode thread — streams decoded PCM to OpenAL
 * ================================================================ */

/* Helper: queue a buffer of PCM to the source */
static int stream_buffer(ALuint source, ALuint buffer,
                         const short *pcm, int frames, int sampleRate, int channels)
{
    if (frames <= 0) return 0;
    ALenum format = (channels >= 2) ? AL_FORMAT_STEREO16 : AL_FORMAT_MONO16;
    int byteSize = frames * channels * (int)sizeof(short);
    alBufferData(buffer, format, pcm, byteSize, sampleRate);
    alSourceQueueBuffers(source, 1, &buffer);
    return 1;
}

/* Helper struct for decoded audio data */
typedef struct {
    short *samples;
    int    totalFrames;
    int    sampleRate;
    int    channels;
} DecodedAudio;

static DecodedAudio decode_mp3(const char *filepath)
{
    DecodedAudio out = {0};

    FILE *f = fopen(filepath, "rb");
    if (!f) return out;
    fseek(f, 0, SEEK_END);
    long size = ftell(f);
    fseek(f, 0, SEEK_SET);
    if (size <= 0 || size > 100 * 1024 * 1024) { fclose(f); return out; }

    unsigned char *fileData = (unsigned char *)malloc(size);
    if (!fileData) { fclose(f); return out; }
    fread(fileData, 1, size, f);
    fclose(f);

    mp3dec_t dec;
    mp3dec_init(&dec);

    /* First pass: count frames to pre-allocate */
    int totalSamples = 0;
    int capacity = 0;
    short *allSamples = NULL;

    size_t offset = 0;
    mp3dec_frame_info_t info;
    short frameBuf[MINIMP3_MAX_SAMPLES_PER_FRAME];

    while (offset < (size_t)size) {
        int samples = mp3dec_decode_frame(&dec, fileData + offset,
                                          (int)(size - offset), frameBuf, &info);
        if (info.frame_bytes <= 0) break;
        offset += info.frame_bytes;

        if (samples > 0) {
            int needed = totalSamples + samples * info.channels;
            if (needed > capacity) {
                capacity = needed * 2 + 44100 * 2;
                allSamples = (short *)realloc(allSamples, capacity * sizeof(short));
                if (!allSamples) { free(fileData); return out; }
            }
            memcpy(allSamples + totalSamples, frameBuf,
                   samples * info.channels * sizeof(short));
            totalSamples += samples * info.channels;

            out.sampleRate = info.hz;
            out.channels = info.channels;
        }
    }

    free(fileData);

    if (totalSamples > 0 && out.channels > 0) {
        out.samples = allSamples;
        out.totalFrames = totalSamples / out.channels;
    } else {
        free(allSamples);
    }

    return out;
}

static DecodedAudio decode_wav(const char *filepath)
{
    DecodedAudio out = {0};

    unsigned int channels, sampleRate;
    drwav_uint64 totalFrameCount;
    short *samples = drwav_open_file_and_read_pcm_frames_s16(
        filepath, &channels, &sampleRate, &totalFrameCount, NULL);

    if (samples && totalFrameCount > 0) {
        out.samples = samples;
        out.totalFrames = (int)totalFrameCount;
        out.sampleRate = (int)sampleRate;
        out.channels = (int)channels;
    }

    return out;
}

static DecodedAudio decode_ogg(const char *filepath)
{
    DecodedAudio out = {0};

    int channels, sampleRate;
    short *samples;
    int totalFrames = stb_vorbis_decode_filename(filepath, &channels,
                                                  &sampleRate, &samples);
    if (totalFrames > 0 && samples) {
        out.samples = samples;
        out.totalFrames = totalFrames;
        out.sampleRate = sampleRate;
        out.channels = channels;
    }

    return out;
}

static void *decode_thread_func(void *arg)
{
    int ch = (int)(long)arg;

    pthread_mutex_lock(&g_mutex);
    char *filepath = g_channels[ch].filepath ? strdup(g_channels[ch].filepath) : NULL;
    int looping = g_channels[ch].looping;
    ALuint source = g_channels[ch].source;
    float volume = g_channels[ch].volume;
    pthread_mutex_unlock(&g_mutex);

    if (!filepath) goto done;

    /* Determine format from extension */
    const char *ext = strrchr(filepath, '.');
    if (!ext) { free(filepath); goto done; }

    DecodedAudio audio = {0};

    if (strcasecmp(ext, ".mp3") == 0)
        audio = decode_mp3(filepath);
    else if (strcasecmp(ext, ".wav") == 0)
        audio = decode_wav(filepath);
    else if (strcasecmp(ext, ".ogg") == 0)
        audio = decode_ogg(filepath);

    free(filepath);

    if (!audio.samples || audio.totalFrames <= 0)
        goto done;

    /* Stream the decoded data to OpenAL in chunks */
    do {
        int framePos = 0;

        /* Pre-fill the ring of stream buffers */
        pthread_mutex_lock(&g_mutex);
        int numBufs = 0;
        for (int b = 0; b < STREAM_BUFS && framePos < audio.totalFrames; b++) {
            int frames = audio.totalFrames - framePos;
            if (frames > STREAM_BUF_FRAMES) frames = STREAM_BUF_FRAMES;

            short *ptr = audio.samples + framePos * audio.channels;
            stream_buffer(source, g_channels[ch].streamBufs[b],
                          ptr, frames, audio.sampleRate, audio.channels);
            feed_snapshot(ch, ptr, frames, audio.channels);
            framePos += frames;
            numBufs++;
        }

        alSourcef(source, AL_GAIN, volume);
        alSourcePlay(source);
        pthread_mutex_unlock(&g_mutex);

        /* Streaming loop: wait for buffers to be processed, refill */
        while (!g_channels[ch].stopRequested) {
            ALint processed = 0;
            alGetSourcei(source, AL_BUFFERS_PROCESSED, &processed);

            while (processed-- > 0) {
                ALuint buf;
                alSourceUnqueueBuffers(source, 1, &buf);

                if (framePos < audio.totalFrames) {
                    int frames = audio.totalFrames - framePos;
                    if (frames > STREAM_BUF_FRAMES) frames = STREAM_BUF_FRAMES;

                    short *ptr = audio.samples + framePos * audio.channels;

                    pthread_mutex_lock(&g_mutex);
                    stream_buffer(source, buf, ptr, frames,
                                  audio.sampleRate, audio.channels);
                    feed_snapshot(ch, ptr, frames, audio.channels);
                    volume = g_channels[ch].volume;
                    alSourcef(source, AL_GAIN, volume);
                    pthread_mutex_unlock(&g_mutex);

                    framePos += frames;
                }
            }

            /* Check if source has stopped (all buffers consumed) */
            ALint state = 0;
            alGetSourcei(source, AL_SOURCE_STATE, &state);
            if (state == AL_STOPPED && framePos >= audio.totalFrames)
                break;

            /* If source stopped due to buffer underrun but we have more data, restart */
            if (state == AL_STOPPED && framePos < audio.totalFrames) {
                alSourcePlay(source);
            }

            usleep(10000); /* 10ms sleep between polls */
        }
    } while (looping && !g_channels[ch].stopRequested);

    free(audio.samples);

done:
    pthread_mutex_lock(&g_mutex);
    g_channels[ch].decodeThreadRunning = 0;
    pthread_mutex_unlock(&g_mutex);
    return NULL;
}
