# AudioWatchdog

Recovers LCPDFR's audio after it silently stops.

## The problem

`LCPDFR/AudioHelper.cs` guards every audio path with a single private static bool:

    if (isBusy) return;
    isBusy = true;
    ... play ...
    isBusy = false;

There are around 90 assignments of it. Any path that wedges or exits without resetting it
leaves it `true` for the rest of the session, and every later sound is skipped at the
guard, before a sound device is ever created.

`Engine/IO/SoundEngine.cs` `PlayExternalSound` is the obvious candidate, because it waits
for playback to start with no timeout:

    while (!secondarySoundBuffer.Status.HasFlag(BufferStatus.Playing)) { Thread.Sleep(1); }

Observed in practice as a callout displaying with no audio, while the Wine mixer-thread
count stayed flat and nothing was logged anywhere, meaning LCPDFR never asked for a sound
at all.

## The fix

A plugin that resets that flag once it has been set for too long with nothing actually
playing.

It writes one boolean and never disposes anything, which is deliberate. Two earlier
attempts at releasing LCPDFR's leaked SlimDX objects both caused new failures of their
own, so this does the minimum that restores audio and nothing more.

## Building

Needs `mono-devel` for `mcs`, and an installed LCPDFR for the reference assemblies.

    ./build.sh                 # finds GTA IV through Steam's library list
    ./build.sh /path/to/GTAIV  # or point it at the game yourself

Then copy `AudioWatchdog.dll` into the game's `plugins/` folder.

## Scope

GTA IV 1.0.7.0 with LCPDFR 1.1 under Proton. This treats a symptom. The underlying leak is
fixed properly at source in [lcpdfr-1.1-audiofix](https://github.com/ipkpjersi/lcpdfr-1.1-audiofix).
