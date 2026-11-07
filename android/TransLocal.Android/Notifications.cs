using Android.App;
using Android.Content;
using TransLocal.Streaming;

namespace TransLocal.Mobile;

internal static class Notifications
{
    public const string ServiceChannel = "translocal_service";
    public const string RequestChannel = "translocal_requests";
    public const int ServiceId = 1;

    public static void EnsureChannels(Context ctx)
    {
        var nm = (NotificationManager)ctx.GetSystemService(Context.NotificationService)!;
        nm.CreateNotificationChannel(new NotificationChannel(ServiceChannel, "Conexão com o computador", NotificationImportance.Low)
        {
            Description = "Mantém o celular pronto para aprovar pedidos de acesso.",
        });
        var requests = new NotificationChannel(RequestChannel, "Pedidos de acesso", NotificationImportance.High)
        {
            Description = "Avisa quando um aparelho pede para acessar o seu computador.",
        };
        requests.EnableVibration(true);
        nm.CreateNotificationChannel(requests);
    }

    private static PendingIntent OpenApp(Context ctx)
    {
        var intent = new Intent(ctx, typeof(MainActivity)).AddFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        return PendingIntent.GetActivity(ctx, 0, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
    }

    public static Notification Service(Context ctx, string text) =>
        new Notification.Builder(ctx, ServiceChannel)
            .SetContentTitle("TransLocal")!
            .SetContentText(text)!
            .SetSmallIcon(Resource.Mipmap.appicon)!
            .SetOngoing(true)!
            .SetContentIntent(OpenApp(ctx))!
            .Build()!;

    public static void UpdateService(Context ctx, string text)
    {
        var nm = (NotificationManager)ctx.GetSystemService(Context.NotificationService)!;
        nm.Notify(ServiceId, Service(ctx, text));
    }

    private static int IdFor(string requestId) => 1000 + (requestId.GetHashCode() & 0x7FFF);

    public static void ShowRequest(Context ctx, ApprovalRequestMessage r, string pcName)
    {
        PendingIntent Action(string action, int code)
        {
            var intent = new Intent(ctx, typeof(ApprovalActionReceiver)).SetAction(action).PutExtra("id", r.Id);
            return PendingIntent.GetBroadcast(ctx, code, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
        }

        Notification.Action AllowAction(int id)
        {
            var b = new Notification.Action.Builder(null, "Permitir", Action(ApprovalActionReceiver.Approve, id * 2));
            // Android 12+: aprovar exige desbloquear o celular — quem pegar o aparelho bloqueado não libera o acesso.
            if (OperatingSystem.IsAndroidVersionAtLeast(31)) b.SetAuthenticationRequired(true);
            return b.Build()!;
        }

        int nid = IdFor(r.Id);
        var n = new Notification.Builder(ctx, RequestChannel)
            .SetContentTitle("Pedido de acesso")!
            .SetContentText($"{r.DeviceName} quer acessar {pcName}")!
            .SetStyle(new Notification.BigTextStyle().BigText(
                $"{r.DeviceName} (ID {r.DeviceId}) quer ver e usar a tela de {pcName}. Permita só se foi você."))!
            .SetSmallIcon(Resource.Mipmap.appicon)!
            .SetCategory(Notification.CategoryCall)!
            .SetVisibility(NotificationVisibility.Private)!
            .SetAutoCancel(true)!
            .SetTimeoutAfter(Math.Max(10, r.TimeoutSeconds) * 1000L)!
            .SetContentIntent(OpenApp(ctx))!
            .AddAction(AllowAction(nid))!
            .AddAction(new Notification.Action.Builder(null, "Negar", Action(ApprovalActionReceiver.Deny, nid * 2 + 1)).Build())!
            .Build()!;
        var nm = (NotificationManager)ctx.GetSystemService(Context.NotificationService)!;
        nm.Notify(nid, n);
    }

    public static void CancelRequest(Context ctx, string requestId)
    {
        var nm = (NotificationManager)ctx.GetSystemService(Context.NotificationService)!;
        nm.Cancel(IdFor(requestId));
    }
}

/// <summary>Botões “Permitir / Negar” da notificação.</summary>
[BroadcastReceiver(Exported = false)]
internal sealed class ApprovalActionReceiver : BroadcastReceiver
{
    public const string Approve = "com.jefzinw.translocal.APPROVE";
    public const string Deny = "com.jefzinw.translocal.DENY";

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent?.GetStringExtra("id") is not { } id) return;
        bool approve = intent.Action == Approve;
        var pending = GoAsync();
        var app = context.ApplicationContext!;
        Task.Run(() =>
        {
            try
            {
                if (!PhoneState.Respond(id, approve))
                    global::Android.OS.Handler.CreateAsync(global::Android.OS.Looper.MainLooper!).Post(() =>
                        global::Android.Widget.Toast.MakeText(app, "Sem conexão com o computador: o pedido não foi respondido.", global::Android.Widget.ToastLength.Long)!.Show());
                Notifications.CancelRequest(app, id);
            }
            finally
            {
                pending?.Finish();
            }
        });
    }
}
