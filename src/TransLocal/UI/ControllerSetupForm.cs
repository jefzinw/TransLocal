using TransLocal.Input;

namespace TransLocal.UI;

/// <summary>
/// Assistente que ensina ao TransLocal os botões de um controle não reconhecido (ex.: controles Bluetooth genéricos):
/// pede cada botão/eixo, detecta o que mudou no joystick e gera um mapeamento no formato da SDL.
/// </summary>
internal sealed class ControllerSetupForm : Form
{
    private enum Kind { Button, Trigger, Stick }

    private sealed record Step(string Key, string Prompt, Kind Kind, bool Optional = false);

    private static readonly Step[] Steps =
    {
        new("a", "Aperte o botão de BAIXO entre os quatro da direita\n(A no Xbox, ✕ no PlayStation)", Kind.Button),
        new("b", "Aperte o botão da DIREITA entre os quatro\n(B no Xbox, ○ no PlayStation)", Kind.Button),
        new("x", "Aperte o botão da ESQUERDA entre os quatro\n(X no Xbox, □ no PlayStation)", Kind.Button),
        new("y", "Aperte o botão de CIMA entre os quatro\n(Y no Xbox, △ no PlayStation)", Kind.Button),
        new("leftshoulder", "Aperte o botão de ombro ESQUERDO (LB / L1)", Kind.Button),
        new("rightshoulder", "Aperte o botão de ombro DIREITO (RB / R1)", Kind.Button),
        new("lefttrigger", "Aperte o gatilho ESQUERDO até o fim (LT / L2)", Kind.Trigger),
        new("righttrigger", "Aperte o gatilho DIREITO até o fim (RT / R2)", Kind.Trigger),
        new("back", "Aperte SELECT / BACK / SHARE", Kind.Button),
        new("start", "Aperte START / OPTIONS", Kind.Button),
        new("guide", "Aperte o botão central (HOME / PS / Xbox)\n— ou clique em Pular", Kind.Button, Optional: true),
        new("leftstick", "Aperte o analógico ESQUERDO para dentro (L3)", Kind.Button, Optional: true),
        new("rightstick", "Aperte o analógico DIREITO para dentro (R3)", Kind.Button, Optional: true),
        new("dpup", "Aperte o direcional para CIMA", Kind.Button),
        new("dpdown", "Aperte o direcional para BAIXO", Kind.Button),
        new("dpleft", "Aperte o direcional para a ESQUERDA", Kind.Button),
        new("dpright", "Aperte o direcional para a DIREITA", Kind.Button),
        new("leftx", "Empurre o analógico ESQUERDO para a DIREITA", Kind.Stick),
        new("lefty", "Empurre o analógico ESQUERDO para BAIXO", Kind.Stick),
        new("rightx", "Empurre o analógico DIREITO para a DIREITA", Kind.Stick, Optional: true),
        new("righty", "Empurre o analógico DIREITO para BAIXO", Kind.Stick, Optional: true),
    };

    private const int Threshold = 16000;
    private const int RestTolerance = 8000;

    private readonly ControllerService _service;
    private readonly ControllerInfo _controller;
    private readonly Action<string> _saveMapping;
    private readonly Label _title = new();
    private readonly Label _prompt = new();
    private readonly Label _status = new();
    private readonly Button _btnSkip = new();
    private readonly Button _btnCancel = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 30 };
    private readonly List<string> _bindings = new();

    private RawJoystickState? _baseline;
    private int _step = -1;
    private bool _waitRelease;
    private string? _pendingButton;
    private long _pendingSince;
    private long _startedAt;

    public ControllerSetupForm(ControllerService service, ControllerInfo controller, Action<string> saveMapping)
    {
        _service = service;
        _controller = controller;
        _saveMapping = saveMapping;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        Text = "Configurar controle — TransLocal";
        Icon = SystemIntegration.AppIcon;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ClientSize = new Size(560, 300);

        _title.SetBounds(20, 16, 520, 24);
        _title.Font = new Font("Segoe UI Semibold", 10.5F);
        _title.Text = controller.Name;
        _prompt.SetBounds(20, 56, 520, 110);
        _prompt.Font = new Font("Segoe UI", 15F);
        _prompt.TextAlign = ContentAlignment.MiddleCenter;
        _status.SetBounds(20, 176, 520, 44);
        _status.ForeColor = Color.FromArgb(96, 96, 96);
        _status.TextAlign = ContentAlignment.MiddleCenter;

        _btnSkip.Text = "Pular";
        _btnSkip.SetBounds(330, 244, 100, 32);
        _btnSkip.Click += (_, _) => Skip();
        _btnCancel.Text = "Cancelar";
        _btnCancel.SetBounds(440, 244, 100, 32);
        _btnCancel.Click += (_, _) => Close();
        CancelButton = _btnCancel;

        Controls.AddRange(new Control[] { _title, _prompt, _status, _btnSkip, _btnCancel });
        ResumeLayout(false);

        _timer.Tick += (_, _) => Tick();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _service.WatchRaw(_controller.Id);
        _prompt.Text = "Solte o controle e não toque em nada…";
        _status.Text = "Medindo a posição de repouso dos analógicos e gatilhos.";
        _btnSkip.Enabled = false;
        _startedAt = Environment.TickCount64;
        _timer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        _service.WatchRaw(0);
        base.OnFormClosed(e);
    }

    private void Tick()
    {
        var raw = _service.Raw;
        if (raw is null || raw.Id != _controller.Id) return;

        if (_baseline is null)
        {
            // Espera ~1 s com o controle parado para registrar o repouso.
            if (Environment.TickCount64 - _startedAt < 1000) return;
            if (raw.Buttons.Any(b => b))
            {
                _status.Text = "Há um botão pressionado — solte todos os botões.";
                return;
            }
            _baseline = raw;
            NextStep();
            return;
        }

        if (_waitRelease)
        {
            if (IsAtRest(raw))
            {
                _waitRelease = false;
                NextStep();
            }
            return;
        }

        var step = Steps[_step];
        string? binding = Detect(raw, step);
        if (binding is null) return;
        _bindings.Add($"{step.Key}:{binding}");
        _status.Text = $"Reconhecido ({Describe(binding)}). Solte para continuar.";
        _waitRelease = true;
    }

    private string? Detect(RawJoystickState raw, Step step)
    {
        var b0 = _baseline!;
        int pressed = -1;
        for (int i = 0; i < raw.Buttons.Length && i < b0.Buttons.Length; i++)
            if (raw.Buttons[i] && !b0.Buttons[i]) { pressed = i; break; }

        int axis = -1, best = 0;
        for (int i = 0; i < raw.Axes.Length && i < b0.Axes.Length; i++)
        {
            int d = Math.Abs(raw.Axes[i] - b0.Axes[i]);
            if (d > Threshold && d > best) { axis = i; best = d; }
        }

        int hat = -1;
        for (int i = 0; i < raw.Hats.Length && i < b0.Hats.Length; i++)
            if (raw.Hats[i] != b0.Hats[i] && raw.Hats[i] is 1 or 2 or 4 or 8) { hat = i; break; }

        switch (step.Kind)
        {
            case Kind.Stick:
                if (axis < 0) return null;
                return raw.Axes[axis] > b0.Axes[axis] ? $"a{axis}" : $"a{axis}~";

            case Kind.Trigger:
                if (axis >= 0)
                {
                    _pendingButton = null;
                    if (b0.Axes[axis] < -Threshold) return $"a{axis}";          // repousa no mínimo: eixo inteiro
                    return raw.Axes[axis] > b0.Axes[axis] ? $"+a{axis}" : $"-a{axis}";
                }
                if (pressed >= 0)
                {
                    // Alguns gatilhos têm botão e eixo: espera um pouco pelo eixo (analógico é melhor).
                    if (_pendingButton is null)
                    {
                        _pendingButton = $"b{pressed}";
                        _pendingSince = Environment.TickCount64;
                        return null;
                    }
                    if (Environment.TickCount64 - _pendingSince < 250) return null;
                    var r = _pendingButton;
                    _pendingButton = null;
                    return r;
                }
                return null;

            default:
                if (pressed >= 0) return $"b{pressed}";
                if (hat >= 0) return $"h{hat}.{raw.Hats[hat]}";
                if (axis >= 0) return raw.Axes[axis] > b0.Axes[axis] ? $"+a{axis}" : $"-a{axis}";
                return null;
        }
    }

    private bool IsAtRest(RawJoystickState raw)
    {
        var b0 = _baseline!;
        for (int i = 0; i < raw.Buttons.Length && i < b0.Buttons.Length; i++)
            if (raw.Buttons[i] && !b0.Buttons[i]) return false;
        for (int i = 0; i < raw.Hats.Length && i < b0.Hats.Length; i++)
            if (raw.Hats[i] != b0.Hats[i]) return false;
        for (int i = 0; i < raw.Axes.Length && i < b0.Axes.Length; i++)
            if (Math.Abs(raw.Axes[i] - b0.Axes[i]) > RestTolerance) return false;
        return true;
    }

    private void Skip()
    {
        if (_step < 0 || _step >= Steps.Length) return;
        _pendingButton = null;
        _waitRelease = false;
        NextStep();
    }

    private void NextStep()
    {
        _step++;
        if (_step >= Steps.Length)
        {
            Finish();
            return;
        }
        var s = Steps[_step];
        _prompt.Text = s.Prompt;
        _status.Text = $"Passo {_step + 1} de {Steps.Length}" + (s.Optional ? " • opcional" : "");
        _btnSkip.Enabled = true;
    }

    private void Finish()
    {
        _timer.Stop();
        _btnSkip.Enabled = false;
        if (_bindings.Count < 4)
        {
            _prompt.Text = "Poucos controles foram configurados.";
            _status.Text = "Nada foi salvo. Tente de novo.";
            _btnCancel.Text = "Fechar";
            return;
        }
        string name = _controller.Name.Replace(",", " ");
        string mapping = $"{_controller.Guid},{name},{string.Join(",", _bindings)},platform:Windows,";
        _saveMapping(mapping);
        _service.AddMapping(mapping);
        Log.Info($"Controle configurado: {mapping}");
        _prompt.Text = "Pronto! O controle foi configurado.";
        _status.Text = "Ele já pode ser usado na transmissão (o ajuste fica salvo).";
        _btnCancel.Text = "Concluir";
    }

    private static string Describe(string binding) => binding[0] switch
    {
        'b' => $"botão {binding[1..]}",
        'h' => "direcional",
        _ => "eixo " + binding.TrimStart('+', '-', 'a').TrimEnd('~'),
    };
}
