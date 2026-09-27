using System.IO;
using System.Runtime.InteropServices;
using AIUsageMonitor.Core.Notifications;

namespace AIUsageMonitor.App.Notifications;

/// <summary>
/// I due suoni dell'app (spec 2026-09-27 §8): <c>done.wav</c>, morbido, per "Finito" e <c>attention.wav</c>, piu'
/// marcato, per permessi, piani, domande ed errori. Li genera <c>tools/sounds/generate-sounds.js</c> e sono incorporati
/// nell'exe come risorse "Sounds.&lt;file&gt;" (voce EmbeddedResource del progetto). Li riproduce <c>PlaySound</c> di winmm
/// con <c>SND_MEMORY | SND_ASYNC</c>: passano dalla sessione audio dell'app, quindi seguono il volume di sistema e il
/// mixer per app. I byte di ogni suono si caricano una volta sola, al primo uso, in un buffer fissato in memoria per tutta
/// la vita del processo: con <c>SND_ASYNC</c> winmm continua a leggerlo dopo il ritorno della chiamata, e un array gestito
/// non fissato potrebbe essere spostato dal GC a meta' suono. Un suono nuovo interrompe quello in corso (Windows ne suona
/// uno per processo). Non solleva mai: un errore lascia la card muta e va una volta sola a <c>logOnce</c>, con il solo tipo
/// dell'eccezione; un suono che non si carica resta muto fino al riavvio.
/// </summary>
public static class NotificationSound
{
    private const uint SND_ASYNC = 0x0001;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_MEMORY = 0x0004;

    private static readonly Dictionary<NotificationSoundKind, IntPtr> Buffers = new();
    private static int _failureReported;

    public static void Play(NotificationSoundKind kind, Action<string>? logOnce = null)
    {
        if (kind == NotificationSoundKind.None) return;
        try
        {
            var buffer = Buffer(kind, logOnce);
            if (buffer == IntPtr.Zero) return;
            if (!PlaySound(buffer, IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT))
                ReportOnce(logOnce, "PlaySound non riuscito");
        }
        catch (Exception ex)
        {
            ReportOnce(logOnce, ex.GetType().Name);
        }
    }

    /// <summary>Byte del suono fissati in memoria (mai rilasciati: servono fino all'uscita), IntPtr.Zero se non disponibili.</summary>
    private static IntPtr Buffer(NotificationSoundKind kind, Action<string>? logOnce)
    {
        lock (Buffers)
        {
            if (Buffers.TryGetValue(kind, out var loaded)) return loaded;
            var pointer = IntPtr.Zero;
            try
            {
                using var stream = typeof(NotificationSound).Assembly.GetManifestResourceStream(ResourceName(kind));
                if (stream is null)
                {
                    ReportOnce(logOnce, $"risorsa {ResourceName(kind)} mancante");
                }
                else
                {
                    var bytes = new byte[stream.Length];
                    stream.ReadExactly(bytes);
                    pointer = GCHandle.Alloc(bytes, GCHandleType.Pinned).AddrOfPinnedObject();
                }
            }
            catch (Exception ex)
            {
                pointer = IntPtr.Zero;
                ReportOnce(logOnce, ex.GetType().Name);
            }
            Buffers[kind] = pointer;
            return pointer;
        }
    }

    /// <summary>Nome della risorsa: LogicalName "Sounds.%(Filename)%(Extension)" nel progetto dell'app.</summary>
    private static string ResourceName(NotificationSoundKind kind) =>
        kind == NotificationSoundKind.Done ? "Sounds.done.wav" : "Sounds.attention.wav";

    private static void ReportOnce(Action<string>? logOnce, string what)
    {
        if (logOnce is null || Interlocked.Exchange(ref _failureReported, 1) != 0) return;
        try { logOnce(what); }
        catch (Exception) { /* un log che fallisce non deve togliere la card */ }
    }

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);
}
