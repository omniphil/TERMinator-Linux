#ifndef AUDIO_BRIDGE_H
#define AUDIO_BRIDGE_H

#ifdef __cplusplus
extern "C" {
#endif

/**
 * Audio Bridge — OpenAL Soft backend for TERMinator Linux.
 *
 * Provides 7 channels:
 *   0-3: TAP+ audio (streaming file playback or PCM push)
 *   4:   Bell sound (one-shot PCM)
 *   5:   About box music (queued PCM push)
 *   6:   SyncTERM door audio (APC mix, queued PCM push -- AudioService.SyncTermAudioLoop)
 *
 * Thread-safe: all public functions guard shared state with a mutex.
 */

/* Lifecycle */
int  audio_bridge_init(void);
void audio_bridge_shutdown(void);

/* PCM playback — fire-and-forget (bells) or streaming push (MOD render loop) */
int  audio_bridge_play_pcm(int channel, const short *samples, int frames,
                           int sampleRate, int numChannels);

/* Queued PCM push — appends to the channel's buffer queue so consecutive chunks play gapless (up to 4 queued).
   Returns 1 if queued, 0 if the queue is full (retry once some has played) or on error. */
int  audio_bridge_queue_pcm(int channel, const short *samples, int frames,
                            int sampleRate, int numChannels);

/* File playback — decode + stream MP3/WAV/OGG in a background thread */
int  audio_bridge_play_file(int channel, const char *filepath, int loop);

/* Transport controls */
void audio_bridge_stop(int channel);
void audio_bridge_stop_all(void);
void audio_bridge_pause(int channel);
void audio_bridge_resume(int channel);

/* Volume: 0.0 .. 1.0 */
void audio_bridge_set_volume(int channel, float volume);

/* Seek to position in milliseconds (file playback only) */
void audio_bridge_seek(int channel, int positionMs);

/* Query state */
int  audio_bridge_is_playing(int channel);

/* Analyzer feed — copies last N frames of PCM from the channel's ring buffer */
int  audio_bridge_get_pcm_snapshot(int channel, short *buffer, int maxFrames);

/* Short overlapping clips (key clicks). A clip is uploaded once into its own buffer (clipId 0-15); each play takes
   a free source from a small pool, so clips overlap instead of cutting each other off the way play_pcm does on a
   channel. Gain: 0.0 .. 1.0. */
int  audio_bridge_load_clip(int clipId, const short *samples, int frames, int sampleRate, int numChannels);
int  audio_bridge_play_clip(int clipId, float gain);

#ifdef __cplusplus
}
#endif

#endif /* AUDIO_BRIDGE_H */
