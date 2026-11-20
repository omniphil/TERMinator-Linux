cmake_minimum_required(VERSION 3.22.1)
project(syncterm-native LANGUAGES C)

# Compiler flags
set(CMAKE_C_STANDARD 11)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

# GCC/Clang flags
set(CMAKE_C_FLAGS "${CMAKE_C_FLAGS} -Wall -Wno-unused-variable -Wno-unused-function -Wno-missing-braces -Wno-pointer-sign")

# Linux-specific definitions
add_compile_definitions(
    _REENTRANT
    LINUX_CIOLIB
    WPF_CIOLIB
    HAS_INTTYPES_H
    LINK_LIST_THREADSAFE
    XPDEV_THREADSAFE
    L_SET=SEEK_SET
    L_INCR=SEEK_CUR
    L_XTND=SEEK_END
)

# Source directories (relative to project root, then into the TERMinator source)
set(SRC_ROOT ${CMAKE_SOURCE_DIR}/../../TERMinator/src)
set(XPDEV_DIR ${SRC_ROOT}/xpdev)
set(SYNCTERM_DIR ${SRC_ROOT}/syncterm)
set(SBBS3_DIR ${SRC_ROOT}/sbbs3)
set(CONIO_DIR ${SRC_ROOT}/conio)
set(ENCODE_DIR ${SRC_ROOT}/encode)
set(HASH_DIR ${SRC_ROOT}/hash)
set(UIFC_DIR ${SRC_ROOT}/uifc)
set(SFTP_DIR ${SRC_ROOT}/sftp)
set(CRYPTLIB_INCLUDE_DIR ${CMAKE_SOURCE_DIR}/cryptlib/include)

# Include directories
include_directories(
    ${CMAKE_SOURCE_DIR}
    ${XPDEV_DIR}
    ${SYNCTERM_DIR}
    ${SBBS3_DIR}
    ${CONIO_DIR}
    ${ENCODE_DIR}
    ${HASH_DIR}
    ${UIFC_DIR}
    ${SFTP_DIR}
    ${CRYPTLIB_INCLUDE_DIR}
    # SyncTERM APC (apc/) and the headers of the runtime-loaded libsndfile / libjxl it dlopen()s (no link-time
    # dependency: a system without them just doesn't get door audio / JPEG XL)
    ${CMAKE_SOURCE_DIR}/apc
    ${CMAKE_SOURCE_DIR}/apc/include
)

# ============================================================
# libxmp - MOD/S3M/XM/IT tracker playback library
# ============================================================
set(LIBXMP_DIR ${CMAKE_SOURCE_DIR}/../../TERMinator/app/src/main/cpp/libxmp)

set(LIBXMP_SOURCES
    ${LIBXMP_DIR}/virtual.c
    ${LIBXMP_DIR}/format.c
    ${LIBXMP_DIR}/period.c
    ${LIBXMP_DIR}/player.c
    ${LIBXMP_DIR}/read_event.c
    ${LIBXMP_DIR}/dataio.c
    ${LIBXMP_DIR}/misc.c
    ${LIBXMP_DIR}/mkstemp.c
    ${LIBXMP_DIR}/md5.c
    ${LIBXMP_DIR}/lfo.c
    ${LIBXMP_DIR}/scan.c
    ${LIBXMP_DIR}/control.c
    ${LIBXMP_DIR}/far_extras.c
    ${LIBXMP_DIR}/med_extras.c
    ${LIBXMP_DIR}/filter.c
    ${LIBXMP_DIR}/effects.c
    ${LIBXMP_DIR}/mixer.c
    ${LIBXMP_DIR}/mix_all.c
    ${LIBXMP_DIR}/load_helpers.c
    ${LIBXMP_DIR}/load.c
    ${LIBXMP_DIR}/hio.c
    ${LIBXMP_DIR}/hmn_extras.c
    ${LIBXMP_DIR}/extras.c
    ${LIBXMP_DIR}/smix.c
    ${LIBXMP_DIR}/filetype.c
    ${LIBXMP_DIR}/memio.c
    ${LIBXMP_DIR}/tempfile.c
    ${LIBXMP_DIR}/mix_paula.c
    ${LIBXMP_DIR}/miniz_tinfl.c
    # Loaders
    ${LIBXMP_DIR}/loaders/common.c
    ${LIBXMP_DIR}/loaders/iff.c
    ${LIBXMP_DIR}/loaders/itsex.c
    ${LIBXMP_DIR}/loaders/lzw.c
    ${LIBXMP_DIR}/loaders/voltable.c
    ${LIBXMP_DIR}/loaders/sample.c
    ${LIBXMP_DIR}/loaders/vorbis.c
    ${LIBXMP_DIR}/loaders/xm_load.c
    ${LIBXMP_DIR}/loaders/mod_load.c
    ${LIBXMP_DIR}/loaders/s3m_load.c
    ${LIBXMP_DIR}/loaders/stm_load.c
    ${LIBXMP_DIR}/loaders/669_load.c
    ${LIBXMP_DIR}/loaders/far_load.c
    ${LIBXMP_DIR}/loaders/mtm_load.c
    ${LIBXMP_DIR}/loaders/ptm_load.c
    ${LIBXMP_DIR}/loaders/okt_load.c
    ${LIBXMP_DIR}/loaders/ult_load.c
    ${LIBXMP_DIR}/loaders/mdl_load.c
    ${LIBXMP_DIR}/loaders/it_load.c
    ${LIBXMP_DIR}/loaders/stx_load.c
    ${LIBXMP_DIR}/loaders/pt3_load.c
    ${LIBXMP_DIR}/loaders/sfx_load.c
    ${LIBXMP_DIR}/loaders/flt_load.c
    ${LIBXMP_DIR}/loaders/st_load.c
    ${LIBXMP_DIR}/loaders/emod_load.c
    ${LIBXMP_DIR}/loaders/imf_load.c
    ${LIBXMP_DIR}/loaders/digi_load.c
    ${LIBXMP_DIR}/loaders/fnk_load.c
    ${LIBXMP_DIR}/loaders/ice_load.c
    ${LIBXMP_DIR}/loaders/liq_load.c
    ${LIBXMP_DIR}/loaders/ims_load.c
    ${LIBXMP_DIR}/loaders/masi_load.c
    ${LIBXMP_DIR}/loaders/masi16_load.c
    ${LIBXMP_DIR}/loaders/amf_load.c
    ${LIBXMP_DIR}/loaders/stim_load.c
    ${LIBXMP_DIR}/loaders/mmd_common.c
    ${LIBXMP_DIR}/loaders/mmd1_load.c
    ${LIBXMP_DIR}/loaders/mmd3_load.c
    ${LIBXMP_DIR}/loaders/rtm_load.c
    ${LIBXMP_DIR}/loaders/dt_load.c
    ${LIBXMP_DIR}/loaders/no_load.c
    ${LIBXMP_DIR}/loaders/arch_load.c
    ${LIBXMP_DIR}/loaders/sym_load.c
    ${LIBXMP_DIR}/loaders/med2_load.c
    ${LIBXMP_DIR}/loaders/med3_load.c
    ${LIBXMP_DIR}/loaders/med4_load.c
    ${LIBXMP_DIR}/loaders/dbm_load.c
    ${LIBXMP_DIR}/loaders/umx_load.c
    ${LIBXMP_DIR}/loaders/gdm_load.c
    ${LIBXMP_DIR}/loaders/pw_load.c
    ${LIBXMP_DIR}/loaders/gal5_load.c
    ${LIBXMP_DIR}/loaders/gal4_load.c
    ${LIBXMP_DIR}/loaders/mfp_load.c
    ${LIBXMP_DIR}/loaders/asylum_load.c
    ${LIBXMP_DIR}/loaders/muse_load.c
    ${LIBXMP_DIR}/loaders/hmn_load.c
    ${LIBXMP_DIR}/loaders/mgt_load.c
    ${LIBXMP_DIR}/loaders/chip_load.c
    ${LIBXMP_DIR}/loaders/abk_load.c
    ${LIBXMP_DIR}/loaders/coco_load.c
    ${LIBXMP_DIR}/loaders/xmf_load.c
    # ProWizard loaders
    ${LIBXMP_DIR}/loaders/prowizard/prowiz.c
    ${LIBXMP_DIR}/loaders/prowizard/ptktable.c
    ${LIBXMP_DIR}/loaders/prowizard/tuning.c
    ${LIBXMP_DIR}/loaders/prowizard/ac1d.c
    ${LIBXMP_DIR}/loaders/prowizard/di.c
    ${LIBXMP_DIR}/loaders/prowizard/eureka.c
    ${LIBXMP_DIR}/loaders/prowizard/fc-m.c
    ${LIBXMP_DIR}/loaders/prowizard/fuchs.c
    ${LIBXMP_DIR}/loaders/prowizard/fuzzac.c
    ${LIBXMP_DIR}/loaders/prowizard/gmc.c
    ${LIBXMP_DIR}/loaders/prowizard/heatseek.c
    ${LIBXMP_DIR}/loaders/prowizard/ksm.c
    ${LIBXMP_DIR}/loaders/prowizard/mp.c
    ${LIBXMP_DIR}/loaders/prowizard/np1.c
    ${LIBXMP_DIR}/loaders/prowizard/np2.c
    ${LIBXMP_DIR}/loaders/prowizard/np3.c
    ${LIBXMP_DIR}/loaders/prowizard/p61a.c
    ${LIBXMP_DIR}/loaders/prowizard/pm10c.c
    ${LIBXMP_DIR}/loaders/prowizard/pm18a.c
    ${LIBXMP_DIR}/loaders/prowizard/pha.c
    ${LIBXMP_DIR}/loaders/prowizard/prun1.c
    ${LIBXMP_DIR}/loaders/prowizard/prun2.c
    ${LIBXMP_DIR}/loaders/prowizard/tdd.c
    ${LIBXMP_DIR}/loaders/prowizard/unic.c
    ${LIBXMP_DIR}/loaders/prowizard/unic2.c
    ${LIBXMP_DIR}/loaders/prowizard/wn.c
    ${LIBXMP_DIR}/loaders/prowizard/zen.c
    ${LIBXMP_DIR}/loaders/prowizard/tp1.c
    ${LIBXMP_DIR}/loaders/prowizard/tp3.c
    ${LIBXMP_DIR}/loaders/prowizard/p40.c
    ${LIBXMP_DIR}/loaders/prowizard/xann.c
    ${LIBXMP_DIR}/loaders/prowizard/theplayer.c
    ${LIBXMP_DIR}/loaders/prowizard/pp10.c
    ${LIBXMP_DIR}/loaders/prowizard/pp21.c
    ${LIBXMP_DIR}/loaders/prowizard/starpack.c
    ${LIBXMP_DIR}/loaders/prowizard/titanics.c
    ${LIBXMP_DIR}/loaders/prowizard/skyt.c
    ${LIBXMP_DIR}/loaders/prowizard/novotrade.c
    ${LIBXMP_DIR}/loaders/prowizard/hrt.c
    ${LIBXMP_DIR}/loaders/prowizard/noiserun.c
    # Depackers
    ${LIBXMP_DIR}/depackers/depacker.c
    ${LIBXMP_DIR}/depackers/ppdepack.c
    ${LIBXMP_DIR}/depackers/unsqsh.c
    ${LIBXMP_DIR}/depackers/mmcmp.c
    ${LIBXMP_DIR}/depackers/s404_dec.c
    ${LIBXMP_DIR}/depackers/arc.c
    ${LIBXMP_DIR}/depackers/arcfs.c
    ${LIBXMP_DIR}/depackers/arc_unpack.c
    ${LIBXMP_DIR}/depackers/lzx.c
    ${LIBXMP_DIR}/depackers/lzx_unpack.c
    ${LIBXMP_DIR}/depackers/miniz_zip.c
    ${LIBXMP_DIR}/depackers/unzip.c
    ${LIBXMP_DIR}/depackers/gunzip.c
    ${LIBXMP_DIR}/depackers/uncompress.c
    ${LIBXMP_DIR}/depackers/bunzip2.c
    ${LIBXMP_DIR}/depackers/unlha.c
    ${LIBXMP_DIR}/depackers/unxz.c
    ${LIBXMP_DIR}/depackers/xz_dec_lzma2.c
    ${LIBXMP_DIR}/depackers/xz_dec_stream.c
    ${LIBXMP_DIR}/depackers/crc32.c
    ${LIBXMP_DIR}/depackers/xfnmatch.c
    ${LIBXMP_DIR}/depackers/ptpopen.c
    ${LIBXMP_DIR}/depackers/xfd.c
    ${LIBXMP_DIR}/depackers/xfd_link.c
)

# Build libxmp as a static library
add_library(xmp STATIC ${LIBXMP_SOURCES})

target_include_directories(xmp PRIVATE
    ${LIBXMP_DIR}
)

target_compile_definitions(xmp PRIVATE
    LIBXMP_STATIC
    HAVE_POWF=1
)

target_compile_options(xmp PRIVATE
    -Wno-unused-parameter
    -Wno-sign-compare
    -Wno-implicit-function-declaration
    -Wno-unused-but-set-variable
    -w
)

# ============================================================
# SyncTERM native sources
# ============================================================

# xpdev sources - cross-platform utilities
set(XPDEV_SOURCES
    ${XPDEV_DIR}/genwrap.c
    ${XPDEV_DIR}/sockwrap.c
    ${XPDEV_DIR}/threadwrap.c
    ${XPDEV_DIR}/strwrap.c
    ${XPDEV_DIR}/filewrap.c
    ${XPDEV_DIR}/dirwrap.c
    ${XPDEV_DIR}/ini_file.c
    ${XPDEV_DIR}/str_list.c
    ${XPDEV_DIR}/link_list.c
    ${XPDEV_DIR}/semwrap.c
    ${XPDEV_DIR}/xpevent.c
    ${XPDEV_DIR}/datewrap.c
    ${XPDEV_DIR}/xpprintf.c
    ${XPDEV_DIR}/netwrap.c
    ${XPDEV_DIR}/multisock.c
    ${XPDEV_DIR}/xpdatetime.c
    ${XPDEV_DIR}/unicode.c
)

# Encode sources
set(ENCODE_SOURCES
    ${ENCODE_DIR}/utf8.c
    ${ENCODE_DIR}/base64.c
)

# Hash sources
set(HASH_SOURCES
    ${HASH_DIR}/crc16.c
    ${HASH_DIR}/crc32.c
    ${HASH_DIR}/sha1.c
    ${HASH_DIR}/md5.c
)

# SBBS3 sources - telnet protocol and file transfer
set(SBBS3_SOURCES
    ${SBBS3_DIR}/telnet.c
    ${SBBS3_DIR}/zmodem.c
    ${SBBS3_DIR}/xmodem.c
)

# Terminal emulation sources from conio
set(CTERM_SOURCES
    ${CONIO_DIR}/cterm.c
    ${CONIO_DIR}/vidmodes.c
    ${CONIO_DIR}/utf8_codepages.c
    ${CONIO_DIR}/allfonts.c
)

# SyncTERM core sources - connection management (Telnet + SSH)
set(SYNCTERM_SOURCES
    ${SYNCTERM_DIR}/conn.c
    ${SYNCTERM_DIR}/conn_telnet.c
    ${SYNCTERM_DIR}/telnet_io.c
    ${SYNCTERM_DIR}/rlogin.c
    ${SYNCTERM_DIR}/conn_websocket.c
    ${SYNCTERM_DIR}/ssh.c
    ${SYNCTERM_DIR}/telnets.c
    ${SFTP_DIR}/sftp_str.c
    ${SFTP_DIR}/sftp_pkt.c
    ${SFTP_DIR}/sftp_client.c
    ${SFTP_DIR}/sftp_attr.c
)

# Linux bridge sources
set(LINUX_SOURCES
    ${CMAKE_SOURCE_DIR}/syncterm_bridge.c
    ${CMAKE_SOURCE_DIR}/linux_ciolib.c
    ${CMAKE_SOURCE_DIR}/linux_stubs.c
    ${CMAKE_SOURCE_DIR}/zmodem_bridge.c
    ${CMAKE_SOURCE_DIR}/modplayer_bridge.c
    ${CMAKE_SOURCE_DIR}/audio_bridge.c
    ${CMAKE_SOURCE_DIR}/stb_vorbis.c
    # SyncTERM APC: cache, PPM/JXL images, audio (upstream audio_apc.c + xpbeep mixer core), from Windows 2026-09-16
    ${CMAKE_SOURCE_DIR}/apc/apc_bridge.c
    ${CMAKE_SOURCE_DIR}/apc/apc_audio.c
    ${CMAKE_SOURCE_DIR}/apc/apc_mixer.c
    ${CMAKE_SOURCE_DIR}/apc/apc_sndfile.c
    ${CMAKE_SOURCE_DIR}/apc/apc_image.c
    # TRACE (TERMinator Render And Compute Engine) escapes, from Windows native/trace_bridge.c 2026-09-18
    ${CMAKE_SOURCE_DIR}/apc/trace_bridge.c
    # Rainwire's window protocol: the Rainwire:WM APC messages (client-drawn windows), from Windows 2026-09-30
    ${CMAKE_SOURCE_DIR}/apc/wm_bridge.c
    # Rainwire's pictures: the TERMinator:Pic APC commands (cached GIF/PNG over the screen), from Windows 2026-10-04
    ${CMAKE_SOURCE_DIR}/apc/pic_bridge.c
    ${CMAKE_SOURCE_DIR}/apc/ui_bridge.c
    # OSC 8 hyperlinks, OSC 52 clipboard, OSC 0/2 window title, Rainwire:Caps answer, from Windows 2026-10-01 (12.62)
    ${CMAKE_SOURCE_DIR}/apc/osc_ext.c
    # xterm mouse reports for the main terminal, the way SyncTERM sends them, from Windows 2026-09-30
    ${CMAKE_SOURCE_DIR}/mouse_bridge.c
)

# Build shared library (.so)
add_library(syncterm-native SHARED
    ${XPDEV_SOURCES}
    ${ENCODE_SOURCES}
    ${HASH_SOURCES}
    ${SBBS3_SOURCES}
    ${CTERM_SOURCES}
    ${SYNCTERM_SOURCES}
    ${LINUX_SOURCES}
)

# cryptlib is prebuilt per CPU: cryptlib/linux-x86_64/ (the PC build) and cryptlib/linux-aarch64/ (the 64-bit ARM
# build, cross-compiled by ../../TERMinator-ARM/build-cryptlib-arm64.sh from the same patched source; both share
# cryptlib/include/cryptlib.h, whose CRYPTLIB_PATCHES stamp must match). CMAKE_SYSTEM_PROCESSOR is the TARGET CPU, so a
# cross build with TERMinator-ARM/cmake/aarch64-linux-gnu.cmake picks the ARM archive; a plain x64 build is unchanged.
if(CMAKE_SYSTEM_PROCESSOR MATCHES "^(aarch64|arm64|ARM64)$")
    set(CRYPTLIB_ARCH_DIR linux-aarch64)
else()
    set(CRYPTLIB_ARCH_DIR linux-x86_64)
endif()

# Link libraries
target_link_libraries(syncterm-native
    xmp
    ${CMAKE_SOURCE_DIR}/cryptlib/${CRYPTLIB_ARCH_DIR}/libcl.a
    pthread
    openal
    m
    ${CMAKE_DL_LIBS}
)

# Additional target-specific compile definitions
target_compile_definitions(syncterm-native PRIVATE
    CIOLIB_NO_BEEP
    SYNCTERM_BRIDGE_EXPORTS
    WITH_WEBSOCKET
)

# Set output name to produce libsyncterm-native.so
set_target_properties(syncterm-native PROPERTIES
    OUTPUT_NAME "syncterm-native"
    PREFIX "lib"
)
