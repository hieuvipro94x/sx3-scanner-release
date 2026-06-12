using System;
using System.Collections.Concurrent;
using System.IO;
using System.Media;
using System.Threading;
using System.Threading.Tasks;

namespace SX3_SCANER.Helper
{
    internal static class ScanSoundService
    {
        private static readonly ConcurrentQueue<string> PendingSounds =
            new ConcurrentQueue<string>();
        private static readonly SemaphoreSlim SoundSignal =
            new SemaphoreSlim(0);

        static ScanSoundService()
        {
            Task.Run(() => ProcessQueueAsync());
        }

        public static void PlayOk()
        {
            QueueSound("OK.wav");
        }

        public static void PlayNg()
        {
            QueueSound("NG.wav");
        }

        private static void QueueSound(string fileName)
        {
            try
            {
                string path = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "Sounds",
                    fileName);
                if (!File.Exists(path))
                    return;

                PendingSounds.Enqueue(path);
                SoundSignal.Release();
            }
            catch (Exception ex)
            {
                StartupManager.Log(
                    "[ScanSound] Cannot queue " + fileName + ". " + ex.Message);
            }
        }

        private static async Task ProcessQueueAsync()
        {
            while (true)
            {
                await SoundSignal.WaitAsync().ConfigureAwait(false);

                string path;
                while (PendingSounds.TryDequeue(out path))
                {
                    try
                    {
                        using (var player = new SoundPlayer(path))
                        {
                            player.PlaySync();
                        }
                    }
                    catch (Exception ex)
                    {
                        StartupManager.Log(
                            "[ScanSound] Cannot play " +
                            Path.GetFileName(path) + ". " + ex.Message);
                    }
                }
            }
        }
    }
}
