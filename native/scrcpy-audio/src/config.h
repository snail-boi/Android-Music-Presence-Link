#ifndef SC_CONFIG_H
#define SC_CONFIG_H

// Hand-written replacement for the meson-generated config.h.
// Audio-only DLL build for Windows (mingw-w64).

// Must match the version of the scrcpy-server file pushed to the device
#define SCRCPY_VERSION "4.1"

// Not used on Windows (unix install prefix), but referenced by server.c
#define PREFIX "."

// Locate scrcpy-server next to the executable when SCRCPY_SERVER_PATH is
// not set
#define PORTABLE 1

#define DEFAULT_LOCAL_PORT_RANGE_FIRST 27183
#define DEFAULT_LOCAL_PORT_RANGE_LAST 27199

// mingw-w64 provides strdup; the remaining functions checked by meson
// (asprintf, vasprintf, nrand48, jrand48, reallocarray) are provided by
// compat.c
#define HAVE_STRDUP 1

#endif
