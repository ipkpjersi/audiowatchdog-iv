// Recovers LCPDFR audio after it silently stops.
//
// LCPDFR/AudioHelper.cs guards every audio path with a single private static bool:
//
//     if (isBusy) return;
//     isBusy = true;
//     ... play ...
//     isBusy = false;
//
// There are around 90 assignments of it. Any path that wedges or exits without resetting
// leaves it true for the rest of the session, and every later sound is skipped at the
// guard, before a device is ever created. Engine/IO/SoundEngine.cs PlayExternalSound is
// the obvious candidate, since it waits for playback to start with no timeout:
//
//     while (!secondarySoundBuffer.Status.HasFlag(BufferStatus.Playing)) { Thread.Sleep(1); }
//
// Observed 2026-09-13: a callout displayed with no audio while the Wine mixer-thread count
// stayed flat and no error was logged anywhere, meaning LCPDFR never asked for a sound.
//
// This watchdog resets that flag when it has been set too long with nothing actually
// playing. It writes one boolean and never disposes anything, which is deliberate: two
// earlier attempts at releasing leaked SlimDX objects both caused new failures. See
// TODO.md item 2.
//
// Removing the DLL from LCPDFR\plugins fully reverts this.

namespace AudioWatchdog
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Reflection;

    using LCPD_First_Response.Engine;
    using LCPD_First_Response.Engine.Scripting.Plugins;

    using SlimDX;
    using SlimDX.DirectSound;

    [PluginInfo("AudioWatchdog", false, true)]
    public class AudioWatchdog : Plugin
    {
        // Threshold for calling the flag stuck.
        //
        // 45 seconds was too low. On 2026-09-13 it cleared a flag that had been set 45
        // seconds while a sequence was still running; typical sequences finish in 5 to 10
        // seconds and the longest actually observed ran 60. 120 is double that longest
        // case, and still recovers a genuinely stranded flag inside a couple of minutes.
        //
        // Note the still-playing guard does not make a lower value safe: a sequence is
        // silent between its individual sounds, so it can report nothing playing while
        // still in progress. That is how the premature clear got through.
        private const double StuckSeconds = 120;

        private const double CheckIntervalSeconds = 5;

        // Heartbeat cadence for the text log, so a session leaves a usable trace even if
        // nothing ever goes wrong.
        private const double HeartbeatSeconds = 120;

        private FieldInfo busyField;
        private DateTime? busySince;
        private DateTime lastCheck = DateTime.UtcNow;
        private DateTime lastHeartbeat = DateTime.MinValue;
        private DateTime setAt = DateTime.MinValue;
        private int recoveries;
        private bool lastBusy;

        // How long the flag has been set, for the log. Sequence duration is the one number
        // that separates a slow sequence from a stranded flag, so every clear reports it.
        private string SecondsHeld()
        {
            if (this.setAt == DateTime.MinValue)
            {
                return "?";
            }

            return ((int)(DateTime.UtcNow - this.setAt).TotalSeconds).ToString();
        }

        // Its own file rather than LCPDFR.log, which is large, shared and overwritten. This
        // survives a crash and can be read without hunting through the mod's own output.
        private static void Note(string message)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(GTA.Game.InstallFolder, "audiowatchdog.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " - " + message + Environment.NewLine);
            }
            catch (Exception)
            {
                // Logging must never be the thing that breaks a session.
            }
        }

        public override void Initialize()
        {
            this.busyField = FindBusyField();

            if (this.busyField == null)
            {
                Log.Warning("Could not locate AudioHelper's busy flag. Watchdog inactive.", this);
                Note("INACTIVE: could not locate AudioHelper's busy flag");
                return;
            }

            Log.Info("Started. Will clear a stuck audio flag after " + StuckSeconds + "s idle.", this);
            Note("started; will clear a stuck flag after " + StuckSeconds + "s idle, checking every "
                 + CheckIntervalSeconds + "s");
        }

        // The assembly is obfuscated, so the type cannot be found by name. AudioHelper is
        // identified by shape instead: the only type declaring exactly one private static
        // bool, a read-only public static bool property, and a large static API of which
        // several methods take a string. Verified against the shipped 1.1 assembly, where
        // it is the sole match (43 public static methods, 9 taking a string).
        private static FieldInfo FindBusyField()
        {
            try
            {
                Assembly assembly = typeof(Plugin).Assembly;
                foreach (Type type in assembly.GetTypes())
                {
                    FieldInfo[] fields;
                    PropertyInfo[] properties;
                    MethodInfo[] methods;

                    try
                    {
                        fields = type.GetFields(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
                        properties = type.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
                        methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    FieldInfo candidate = null;
                    int boolFields = 0;
                    foreach (FieldInfo field in fields)
                    {
                        if (field.FieldType == typeof(bool))
                        {
                            boolFields++;
                            candidate = field;
                        }
                    }

                    if (boolFields != 1)
                    {
                        continue;
                    }

                    bool hasReadOnlyBoolProperty = false;
                    foreach (PropertyInfo property in properties)
                    {
                        if (property.PropertyType == typeof(bool) && property.CanRead && !property.CanWrite)
                        {
                            hasReadOnlyBoolProperty = true;
                        }
                    }

                    if (!hasReadOnlyBoolProperty || methods.Length < 20)
                    {
                        continue;
                    }

                    int takingString = 0;
                    foreach (MethodInfo method in methods)
                    {
                        ParameterInfo[] parameters = method.GetParameters();
                        if (parameters.Length > 0 && parameters[0].ParameterType == typeof(string))
                        {
                            takingString++;
                        }
                    }

                    if (takingString >= 5)
                    {
                        return candidate;
                    }
                }
            }
            catch (Exception)
            {
            }

            return null;
        }

        public override void Process()
        {
            try
            {
                if (this.busyField == null)
                {
                    return;
                }

                if ((DateTime.UtcNow - this.lastCheck).TotalSeconds < CheckIntervalSeconds)
                {
                    return;
                }

                this.lastCheck = DateTime.UtcNow;
                this.Check();
            }
            catch (Exception ex)
            {
                // An unhandled exception in Process gets the plugin disposed.
                Log.Warning("Check failed: " + ex.Message, this);
            }
        }

        private void Check()
        {
            bool busy;
            try
            {
                busy = (bool)this.busyField.GetValue(null);
            }
            catch (Exception)
            {
                return;
            }

            this.Heartbeat(busy);

            if (!busy)
            {
                if (this.lastBusy)
                {
                    Note("flag cleared normally by LCPDFR after "
                         + SecondsHeld() + "s");
                }

                this.lastBusy = false;
                this.busySince = null;
                return;
            }

            if (!this.lastBusy)
            {
                this.setAt = DateTime.UtcNow;
                Note("flag set; audio sequence started");
            }

            this.lastBusy = true;

            if (this.busySince == null)
            {
                this.busySince = DateTime.UtcNow;
                return;
            }

            if ((DateTime.UtcNow - this.busySince.Value).TotalSeconds < StuckSeconds)
            {
                return;
            }

            // Second opinion before overriding LCPDFR: if any sound buffer still reports
            // playing, audio really is in progress and the flag is doing its job.
            if (AnythingStillPlaying())
            {
                Note("flag set for over " + StuckSeconds + "s but a buffer is still playing; leaving it alone");
                this.busySince = DateTime.UtcNow;
                return;
            }

            try
            {
                this.busyField.SetValue(null, false);
                this.busySince = null;
                this.recoveries++;

                // Without this the next check sees the flag false, finds lastBusy still
                // true, and logs "cleared normally by LCPDFR" for the watchdog's own
                // clear. That made the first recovery impossible to interpret: the
                // spurious line looked like proof LCPDFR had cleared it itself, which
                // would have meant the sequence was merely slow rather than stuck.
                this.lastBusy = false;
                Log.Info(
                    "Audio flag was stuck for over " + StuckSeconds + "s with nothing playing; cleared it. "
                    + "Recoveries this session: " + this.recoveries,
                    this);
                Note("RECOVERED: flag had been set " + SecondsHeld()
                     + "s with nothing playing; cleared it. Recoveries this session: "
                     + this.recoveries
                     + ". If a 'cleared normally' line follows within seconds, LCPDFR was"
                     + " still running the sequence and this was premature.");
            }
            catch (Exception ex)
            {
                Log.Warning("Could not clear the audio flag: " + ex.Message, this);
                Note("FAILED to clear the flag: " + ex.Message);
            }
        }

        // Periodic state line, including live SlimDX object counts, which is the closest
        // thing to a direct read of the leak from inside the process.
        private void Heartbeat(bool busy)
        {
            if ((DateTime.UtcNow - this.lastHeartbeat).TotalSeconds < HeartbeatSeconds)
            {
                return;
            }

            this.lastHeartbeat = DateTime.UtcNow;

            int devices = 0;
            int buffers = 0;
            int playing = 0;
            try
            {
                lock (ObjectTable.SyncObject)
                {
                    foreach (ComObject comObject in new List<ComObject>(ObjectTable.Objects))
                    {
                        if (comObject.Disposed)
                        {
                            continue;
                        }

                        if (comObject is DirectSound)
                        {
                            devices++;
                        }

                        SecondarySoundBuffer buffer = comObject as SecondarySoundBuffer;
                        if (buffer == null)
                        {
                            continue;
                        }

                        buffers++;
                        try
                        {
                            if (buffer.Status.HasFlag(BufferStatus.Playing))
                            {
                                playing++;
                            }
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
            }
            catch (Exception)
            {
                return;
            }

            Note("state: busy=" + busy + " devices=" + devices + " buffers=" + buffers
                 + " playing=" + playing + " recoveries=" + this.recoveries);
        }

        // Read-only inspection of SlimDX's object table. Nothing is disposed here; an
        // object that will not answer is treated as playing, so the flag is left alone.
        private static bool AnythingStillPlaying()
        {
            try
            {
                lock (ObjectTable.SyncObject)
                {
                    foreach (ComObject comObject in new List<ComObject>(ObjectTable.Objects))
                    {
                        SecondarySoundBuffer buffer = comObject as SecondarySoundBuffer;
                        if (buffer == null || comObject.Disposed)
                        {
                            continue;
                        }

                        try
                        {
                            if (buffer.Status.HasFlag(BufferStatus.Playing))
                            {
                                return true;
                            }
                        }
                        catch (Exception)
                        {
                            return true;
                        }
                    }
                }
            }
            catch (Exception)
            {
                return true;
            }

            return false;
        }

        public override void Finally()
        {
        }
    }
}
