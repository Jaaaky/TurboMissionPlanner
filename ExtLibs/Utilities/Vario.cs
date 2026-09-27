using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MissionPlanner.Utilities
{
    public class Vario
    {
        public static int MidTone = 700;
        static float climbrate = 0;
        public static bool run = false;

        public static string Running
        {
            get
            {
                if (run) return "Stop Vario";
                return "Start Vario";
            }
        }

        public static void SetValue(float climbrate)
        {
            Vario.climbrate = climbrate;
        }

        public static Action<int, int> Beep = (note, durationms) => { Console.Beep(note, durationms); };

        public static async void mainloop(object o)
        {
            while (run)
            {
                float note = climbrate * 30 + MidTone;

                // Fork: pace each pass by elapsed time. Console.Beep blocks for
                // its duration on Windows, but under Wine it returns at once
                // (or throws), so the descending branch and any exception
                // looped with no wait and pegged a core.
                var passStart = DateTime.UtcNow;
                int passMs = 100;

                try
                {

                    if (Math.Abs(climbrate) > 0.3)
                    {
                        // freq , duration
                        if (climbrate > 0)
                        {
                            var duration = Math.Max(50, 300 - (int)(climbrate * 5));
                            passMs = duration + 20;
                            Beep((int)note, duration);
                        }
                        else
                        {
                            passMs = 600;
                            Beep((int)note - 50, 600);
                        }
                    }

                }
                catch
                {
                }

                // sleep for whatever the beep did not already take
                var remaining = passMs - (int)(DateTime.UtcNow - passStart).TotalMilliseconds;
                if (remaining > 0)
                    await Task.Delay(remaining).ConfigureAwait(false);
            }
        }

        public static void Start()
        {
            run = true;
            Task.Run(() =>
            {
                mainloop(null);
            });
        }

        public static void Stop()
        {
            run = false;
        }
    }
}
