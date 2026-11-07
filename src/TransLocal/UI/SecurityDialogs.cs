using TransLocal.Security;

namespace TransLocal.UI;

/// <summary>Janela de confirmação simples (pareamento ou pedido de acesso), sempre por cima, com prazo.</summary>
internal sealed class ConfirmationForm : Form
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly Label _countdown = new();
    private readonly string _countdownFormat;
    private int _secondsLeft;

    public bool Accepted { get; private set; }

    public ConfirmationForm(string title, string headline, string? bigText, string details, string acceptText, string rejectText,
        int timeoutSeconds, string countdownFormat)
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        Text = title;
        Icon = SystemIntegration.AppIcon;
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new Size(520, bigText is null ? 230 : 300);
        _countdownFormat = countdownFormat;
        _secondsLeft = timeoutSeconds;

        int y = 18;
        var head = new Label { Text = headline, Font = new Font("Segoe UI Semibold", 11.5F), AutoSize = false, Bounds = new Rectangle(20, y, 480, 48) };
        Controls.Add(head);
        y += 52;
        if (bigText is not null)
        {
            var big = new Label
            {
                Text = bigText,
                Font = new Font("Segoe UI Semibold", 30F),
                TextAlign = ContentAlignment.MiddleCenter,
                Bounds = new Rectangle(20, y, 480, 64),
                ForeColor = Color.FromArgb(0, 103, 192),
            };
            Controls.Add(big);
            y += 70;
        }
        var info = new Label { Text = details, AutoSize = false, Bounds = new Rectangle(20, y, 480, 60), ForeColor = Color.FromArgb(70, 70, 70) };
        Controls.Add(info);
        y += 64;

        _countdown.Bounds = new Rectangle(20, y + 8, 220, 24);
        _countdown.ForeColor = Color.FromArgb(120, 120, 120);
        var accept = new Button { Text = acceptText, Bounds = new Rectangle(250, y, 140, 34), BackColor = Color.FromArgb(0, 103, 192), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        accept.FlatAppearance.BorderSize = 0;
        var reject = new Button { Text = rejectText, Bounds = new Rectangle(398, y, 102, 34) };
        accept.Click += (_, _) => { Accepted = true; Close(); };
        reject.Click += (_, _) => { Accepted = false; Close(); };
        Controls.AddRange(new Control[] { _countdown, accept, reject });
        CancelButton = reject;
        ResumeLayout(false);

        UpdateCountdown();
        _timer.Tick += (_, _) =>
        {
            _secondsLeft--;
            UpdateCountdown();
            if (_secondsLeft <= 0) Close();
        };
    }

    private void UpdateCountdown() => _countdown.Text = string.Format(_countdownFormat, Math.Max(0, _secondsLeft));

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _timer.Start();
        Activate();
        System.Media.SystemSounds.Exclamation.Play();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        base.OnFormClosed(e);
    }

    /// <summary>Mostra a confirmação a partir de qualquer thread e espera a resposta (bloqueante).</summary>
    public static bool Ask(Control uiThread, Func<ConfirmationForm> create, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ConfirmationForm? form = null;
        try
        {
            uiThread.BeginInvoke(() =>
            {
                form = create();
                form.FormClosed += (_, _) => tcs.TrySetResult(form.Accepted);
                form.Show();
            });
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        using (ct.Register(() =>
               {
                   tcs.TrySetResult(false);
                   try { uiThread.BeginInvoke(() => { if (form is { IsDisposed: false }) form.Close(); }); } catch { }
               }))
        {
            return tcs.Task.GetAwaiter().GetResult();
        }
    }

    public static ConfirmationForm ForPairing(PairingPrompt p)
    {
        string kind = p.PeerKind == "phone" ? "o celular" : "o computador";
        return new ConfirmationForm(
            "Parear aparelho — TransLocal",
            $"Parear com {kind} “{p.PeerName}”?",
            p.Code,
            "Confira se o MESMO código aparece na tela do outro aparelho. Se for diferente, cancele: alguém pode estar " +
            "tentando se passar por ele. Depois de pareados, os dois se reconhecem automaticamente.",
            "Os códigos são iguais", "Cancelar", 120, "Expira em {0} s");
    }

    public static ConfirmationForm ForAccess(string deviceName, string shortId) => new(
        "Pedido de acesso — TransLocal",
        $"“{deviceName}” quer ver e usar a tela deste computador.",
        null,
        $"Aparelho pareado (ID {shortId}), pedindo acesso remoto. Permita só se foi você. " +
        "O pedido também pode ser aprovado pelo celular.",
        "Permitir", "Negar", 60, "Recusado automaticamente em {0} s");
}
