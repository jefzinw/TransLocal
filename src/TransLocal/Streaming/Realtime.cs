using System.Runtime.InteropServices;

namespace TransLocal.Streaming;

/// <summary>
/// Ajustes de baixa latência usados por apps de jogos na nuvem/streaming local (Moonlight, Steam Link, Parsec):
/// prioridade multimídia (MMCSS) para as threads de vídeo e o modo "streaming de mídia" do Wi-Fi.
/// Nada é permanente: tudo volta ao normal quando a transmissão termina (ou o TransLocal fecha).
/// </summary>
internal static class Realtime
{
    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

    [DllImport("avrt.dll", SetLastError = true)]
    private static extern bool AvSetMmThreadPriority(IntPtr handle, int priority);

    /// <summary>
    /// Coloca a thread atual no agendador multimídia do Windows ("Games", "Capture", "Playback"…): ela deixa de
    /// esperar atrás de tarefas comuns. Devolve um identificador para <see cref="Revert"/> (zero se indisponível).
    /// </summary>
    public static IntPtr BoostThread(string task, bool high = true)
    {
        try
        {
            uint index = 0;
            var h = AvSetMmThreadCharacteristicsW(task, ref index);
            if (h != IntPtr.Zero && high) AvSetMmThreadPriority(h, 1 /* AVRT_PRIORITY_HIGH */);
            return h;
        }
        catch { return IntPtr.Zero; }
    }

    public static void Revert(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        try { AvRevertMmThreadCharacteristics(handle); } catch { }
    }

    // ---------------- Wi-Fi ----------------

    [DllImport("wlanapi.dll")]
    private static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr handle);

    [DllImport("wlanapi.dll")]
    private static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);

    [DllImport("wlanapi.dll")]
    private static extern int WlanSetInterface(IntPtr handle, ref Guid iface, int opCode, uint dataSize, IntPtr data, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);

    private const int OpcodeMediaStreamingMode = 3; // wlan_intf_opcode_media_streaming_mode
    private static readonly object WlanGate = new();
    private static IntPtr _wlan;
    private static int _wlanUsers;

    /// <summary>
    /// Liga o modo "streaming de mídia" em todas as placas Wi-Fi: o Windows para de fazer varreduras de redes em
    /// segundo plano durante a transmissão (cada varredura causa um atraso de dezenas a centenas de ms a cada
    /// minuto ou dois). Vale enquanto o identificador devolvido não for descartado; o Windows desfaz sozinho.
    /// </summary>
    public static IDisposable WifiLowLatency()
    {
        lock (WlanGate)
        {
            _wlanUsers++;
            if (_wlanUsers == 1) TryEnableWifi();
        }
        return new WifiLease();
    }

    private static void TryEnableWifi()
    {
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out var handle) != 0) return; // sem Wi-Fi / serviço WLAN parado
            if (WlanEnumInterfaces(handle, IntPtr.Zero, out var list) != 0)
            {
                WlanCloseHandle(handle, IntPtr.Zero);
                return;
            }
            int count = 0, changed = 0;
            IntPtr value = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(value, 1);
                count = Marshal.ReadInt32(list);
                // WLAN_INTERFACE_INFO_LIST: dwNumberOfItems, dwIndex, depois itens de 532 bytes (GUID + 256 WCHAR + estado).
                for (int i = 0; i < count; i++)
                {
                    IntPtr item = list + 8 + i * 532;
                    var guid = Marshal.PtrToStructure<Guid>(item);
                    if (WlanSetInterface(handle, ref guid, OpcodeMediaStreamingMode, 4, value, IntPtr.Zero) == 0) changed++;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(value);
                WlanFreeMemory(list);
            }
            if (count == 0)
            {
                WlanCloseHandle(handle, IntPtr.Zero);
                return;
            }
            _wlan = handle;
            Log.Info($"Wi-Fi em modo de streaming (sem varreduras em segundo plano) em {changed} de {count} placa(s)");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Windows sem o serviço de Wi-Fi.
        }
        catch (Exception ex)
        {
            Log.Info($"Modo de streaming do Wi-Fi indisponível: {ex.Message}");
        }
    }

    private sealed class WifiLease : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            lock (WlanGate)
            {
                if (--_wlanUsers > 0 || _wlan == IntPtr.Zero) return;
                try { WlanCloseHandle(_wlan, IntPtr.Zero); } catch { }
                _wlan = IntPtr.Zero;
            }
        }
    }
}
