# Build scrcpy_audio.dll (audio-only scrcpy) with w64devkit (mingw-w64 gcc)
#
# Usage (from this directory, with w64devkit's bin in PATH):
#   make -j
#
# Runtime dependencies of the produced DLL:
#   avcodec-62.dll, avutil-60.dll, swresample-6.dll, SDL3.dll
# (the same DLLs already shipped with scrcpy / in the AMPL Assets folder)

CC ?= gcc

FFMPEG := deps/ffmpeg-n8.1-latest-win64-gpl-shared-8.1
SDL3 := deps/SDL3-3.4.12/x86_64-w64-mingw32

TARGET := build/scrcpy_audio.dll

CFLAGS := -std=c11 -O2 -Wall -Wextra -Wno-unused-parameter -DNDEBUG \
	-DSCA_BUILDING \
	-D_GNU_SOURCE -D_POSIX_C_SOURCE=200809L -D_XOPEN_SOURCE=700 \
	-D_WIN32_WINNT=0x0600 -DWINVER=0x0600 \
	-Isrc -I$(FFMPEG)/include -I$(SDL3)/include

LDFLAGS := -shared -static-libgcc \
	-L$(FFMPEG)/lib -L$(SDL3)/lib \
	-lavcodec -lavutil -lswresample -lSDL3 -lws2_32

SRC := \
	src/scrcpy_audio.c \
	src/audio_regulator.c \
	src/compat.c \
	src/decoder.c \
	src/demuxer.c \
	src/options.c \
	src/packet_merger.c \
	src/server.c \
	src/adb/adb.c \
	src/adb/adb_device.c \
	src/adb/adb_parser.c \
	src/adb/adb_tunnel.c \
	src/trait/frame_source.c \
	src/trait/packet_source.c \
	src/util/audiobuf.c \
	src/util/average.c \
	src/util/command.c \
	src/util/env.c \
	src/util/file.c \
	src/util/intr.c \
	src/util/log.c \
	src/util/memory.c \
	src/util/net.c \
	src/util/net_intr.c \
	src/util/process.c \
	src/util/process_intr.c \
	src/util/rand.c \
	src/util/str.c \
	src/util/strbuf.c \
	src/util/term.c \
	src/util/thread.c \
	src/util/tick.c \
	src/sys/win/file.c \
	src/sys/win/process.c

OBJ := $(SRC:src/%.c=build/obj/%.o)

.PHONY: all clean

all: $(TARGET)

$(TARGET): $(OBJ)
	@mkdir -p $(dir $@)
	$(CC) $(OBJ) -o $@ $(LDFLAGS)

build/obj/%.o: src/%.c
	@mkdir -p $(dir $@)
	$(CC) $(CFLAGS) -c $< -o $@

clean:
	rm -rf build
