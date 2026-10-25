using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LibreHardwareMonitor.Hardware;

namespace ResetExplode;

/// <summary>Item da lista de dispositivos de iluminação (caixa de seleção + nome).</summary>
public sealed class LightDeviceVm
{
    public LightDevice Device { get; init; } = null!;
    public string Name => Device.Name;
    public string Detail => Device.Detail;
    public bool Selected { get; set; } = true;
}

/// <summary>
/// Aba RESET INTEGRAÇÃO: controle de iluminação e efeitos. Pontes: Corsair iCUE (SDK), OpenRGB (MSI Mystic Light, Gigabyte RGB Fusion,
/// ASUS Aura e outras marcas) e Razer Chroma. Todos os dispositivos aparecem numa lista só e recebem o mesmo efeito.
/// </summary>
public partial class MainWindow
{
    private CorsairSdk? _cue;
    private CorsairBackend? _corsair;
    private OpenRgbBackend? _orgb;
    private RazerChromaBackend? _razer;
    private readonly Dictionary<string, List<LightDevice>> _devsByBackend = new();
    private List<LightDeviceVm> _lightVms = new();
    private DispatcherTimer? _lightTimer;
    private bool _lightRunning, _lightBusy, _lightInited, _lightLoading, _lightRename;
    private readonly Dictionary<string, LightDevice> _lightControlled = new();
    private string _effect = "static";
    private (byte R, byte G, byte B) _lightColor = (255, 255, 255);
    private readonly Stopwatch _lightClock = Stopwatch.StartNew();
    private readonly Dictionary<string, ((byte, byte, byte)[] Colors, long At)> _lightLast = new();
    private float _smoothValue = float.NaN;
    private int _lastLightError;
    private readonly Dictionary<string, int> _lightErrByDevice = new();
    private readonly Queue<string> _diag = new();

    /// <summary>Linha do painel de diagnóstico da aba (últimas 80 linhas).</summary>
    private void Diag(string line)
    {
        _diag.Enqueue($"{DateTime.Now:HH:mm:ss}  {line}");
        while (_diag.Count > 80) _diag.Dequeue();
        if (LightDiag != null) { LightDiag.Text = string.Join("\n", _diag); LightDiag.ScrollToEnd(); }
    }

    private static readonly (string Name, byte R, byte G, byte B)[] Swatches =
    {
        ("Branco", 255, 255, 255), ("Vermelho", 255, 0, 0), ("Laranja", 255, 110, 0), ("Amarelo", 255, 210, 0),
        ("Verde", 0, 255, 60), ("Ciano", 0, 220, 255), ("Azul", 0, 80, 255), ("Roxo", 150, 0, 255), ("Rosa", 255, 0, 160),
    };

    private void InitInteg()
    {
        if (_lightInited) return;
        _lightInited = true;
        foreach (var (name, r, g, b) in Swatches)
        {
            var btn = new Button
            {
                Width = 30, Height = 30, Margin = new Thickness(0, 0, 8, 8), Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = name, BorderBrush = (Brush)FindResource("LineStrong"), BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b)),
                Tag = (r, g, b),
                Template = SwatchTemplate(),
            };
            btn.Click += (_, _) => { var (rr, gg, bb) = ((byte, byte, byte))btn.Tag; LightR.Value = rr; LightG.Value = gg; LightB.Value = bb; };
            LightSwatches.Children.Add(btn);
        }
        _lightTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _lightTimer.Tick += LightTick;
        ApplyLightProfile();
        RefreshLightProfiles();
        BtnOrgbOpen.Content = FindOpenRgb() != null ? "ABRIR O OPENRGB" : "BAIXAR O OPENRGB";
        _ = ConnectAllAsync(); // primeira abertura da aba: tenta conectar em tudo
    }

    private static ControlTemplate SwatchTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(15));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    // ---------------- Perfis ----------------

    private void ApplyLightProfile()
    {
        _lightLoading = true;
        try
        {
            var p = LightProfiles.Active(_settings);
            _effect = p.Effect;
            foreach (var radio in LightFxPanel.Children.OfType<RadioButton>())
                radio.IsChecked = (string)radio.Tag == p.Effect;
            LightR.Value = p.R; LightG.Value = p.G; LightB.Value = p.B;
            LightBright.Value = p.Brightness; LightSpeed.Value = p.Speed;
            LightExclusive.IsChecked = p.Exclusive;
            RebuildLightList();
            LightColor_Changed(null, null!);
            _smoothValue = float.NaN;
        }
        finally { _lightLoading = false; }
        UpdateLightKeep();
    }

    private void RefreshLightProfiles()
    {
        _lightLoading = true;
        try
        {
            LightProfilePicker.ItemsSource = null;
            LightProfilePicker.ItemsSource = _settings.LightProfiles;
            LightProfilePicker.SelectedItem = LightProfiles.Active(_settings);
            BtnLightProfileDelete.IsEnabled = _settings.LightProfiles.Count > 1;
        }
        finally { _lightLoading = false; }
    }

    private void SaveLightSettings()
    {
        try { _settings.Save(); }
        catch (Exception ex)
        {
            LightRunHint.Text = "Não consegui salvar os perfis: " + ex.Message;
            Diag(LightRunHint.Text);
        }
    }

    private void SaveLightProfile()
    {
        if (!_lightInited || _lightLoading) return;
        var p = LightProfiles.Active(_settings);
        p.Effect = _effect;
        p.R = (byte)LightR.Value; p.G = (byte)LightG.Value; p.B = (byte)LightB.Value;
        p.Brightness = LightBright.Value; p.Speed = LightSpeed.Value;
        p.Exclusive = LightExclusive.IsChecked == true;
        // Mantém também as escolhas dos dispositivos desconectados.
        var unselected = p.Unselected.ToHashSet();
        foreach (var vm in _lightVms)
        {
            if (vm.Selected) unselected.Remove(vm.Device.Key);
            else unselected.Add(vm.Device.Key);
        }
        p.Unselected = unselected.OrderBy(key => key, StringComparer.Ordinal).ToList();
        SaveLightSettings();
    }

    private void LightProfile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_lightInited || _lightLoading || LightProfilePicker.SelectedItem is not LightProfile p) return;
        bool running = _lightRunning;
        if (running) StopLighting(true);
        _settings.LightProfileActive = p.Name;
        LightProfileEditor.Visibility = Visibility.Collapsed;
        ApplyLightProfile();
        SaveLightSettings();
        if (running) StartLighting();
    }

    private void EditLightProfileName(bool rename)
    {
        _lightRename = rename;
        LightProfileNameLabel.Text = rename ? "RENOMEAR PERFIL" : "NOVO PERFIL · CÓPIA DO ATUAL";
        LightProfileName.Text = rename ? LightProfiles.Active(_settings).Name : "";
        LightProfileNameError.Text = "";
        LightProfileEditor.Visibility = Visibility.Visible;
        LightProfileName.Focus();
        LightProfileName.SelectAll();
    }

    private void BtnLightProfileNew_Click(object sender, RoutedEventArgs e) => EditLightProfileName(false);
    private void BtnLightProfileRename_Click(object sender, RoutedEventArgs e) => EditLightProfileName(true);
    private void BtnLightProfileCancel_Click(object sender, RoutedEventArgs e) => LightProfileEditor.Visibility = Visibility.Collapsed;

    private void BtnLightProfileConfirm_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_lightRename) LightProfiles.Rename(_settings, LightProfiles.Active(_settings), LightProfileName.Text);
            else LightProfiles.Create(_settings, LightProfileName.Text);
        }
        catch (ArgumentException ex) { LightProfileNameError.Text = ex.Message; return; }
        LightProfileEditor.Visibility = Visibility.Collapsed;
        RefreshLightProfiles();
        SaveLightSettings();
    }

    private void LightProfileName_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) { BtnLightProfileConfirm_Click(sender, e); e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.Escape) { LightProfileEditor.Visibility = Visibility.Collapsed; e.Handled = true; }
    }

    private void BtnLightProfileDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.LightProfiles.Count <= 1) return;
        var p = LightProfiles.Active(_settings);
        if (MessageBox.Show(this, $"Excluir o perfil \"{p.Name}\"?", "RESET EXPLODE · Iluminação",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        bool running = _lightRunning;
        if (running) StopLighting(true);
        LightProfiles.Delete(_settings, p);
        LightProfileEditor.Visibility = Visibility.Collapsed;
        ApplyLightProfile();
        RefreshLightProfiles();
        SaveLightSettings();
        if (running) StartLighting();
    }

    private void LightDevice_Click(object sender, RoutedEventArgs e)
    {
        if (!_lightInited || _lightLoading) return;
        if (sender is CheckBox { DataContext: LightDeviceVm vm } box) vm.Selected = box.IsChecked == true;
        SaveLightProfile();
        RestartLightControl();
    }

    private void LightExclusive_Click(object sender, RoutedEventArgs e)
    {
        SaveLightProfile();
        RestartLightControl();
    }

    private void RestartLightControl()
    {
        if (_lightRunning) { StopLighting(true); StartLighting(); }
        UpdateLightKeep();
    }

    private void UpdateLightKeep()
    {
        if (BtnLightKeep == null) return;
        bool fixedEffect = _effect is "static" or "off";
        bool openRgb = _lightVms.Any(v => v.Selected && v.Device.Backend == _orgb && v.Device.LedCount > 0);
        BtnLightKeep.IsEnabled = _lightRunning && fixedEffect && openRgb;
        BtnLightKeep.ToolTip = !fixedEffect
            ? "Respiração, Arco-íris e os efeitos de sensor precisam do app aberto"
            : !_lightRunning ? "Ative o controle antes de deixar as cores no OpenRGB."
            : !openRgb ? "Conecte e marque pelo menos um dispositivo OpenRGB com LEDs."
            : "Deixa as cores e salva o perfil ResetExplode no OpenRGB (todos os controladores).";
    }

    private void BtnLightKeep_Click(object sender, RoutedEventArgs e)
    {
        if (!BtnLightKeep.IsEnabled || _orgb == null) return;
        _lightTimer?.Stop();
        LightOpenRgbGuide.Visibility = Visibility.Collapsed;
        try
        {
            double bright = LightBright.Value / 100.0;
            var color = _effect == "off" ? ((byte)0, (byte)0, (byte)0)
                : ((byte)(_lightColor.R * bright), (byte)(_lightColor.G * bright), (byte)(_lightColor.B * bright));
            foreach (var vm in _lightVms.Where(v => v.Selected && v.Device.Backend == _orgb && v.Device.LedCount > 0))
            {
                var d = vm.Device;
                if (!_lightControlled.ContainsKey(d.Key)) throw new IOException("O OpenRGB não cedeu o controle de " + d.Name + ".");
                var colors = Enumerable.Repeat(color, d.LedCount).ToArray();
                if (_orgb.Send(d, colors) != 0) throw new IOException("Falha ao enviar as cores a " + d.Name + ".");
            }
            _orgb.SaveProfile("ResetExplode");
            StopLighting(true, keepOpenRgb: true);
            LightOpenRgbGuide.Visibility = Visibility.Visible;
            Diag("OpenRGB · cores enviadas; solicitado SAVE_PROFILE ResetExplode (sem confirmação no protocolo).");
        }
        catch (Exception ex)
        {
            LightRunHint.Text = "Não consegui deixar no OpenRGB: " + ex.Message + " O controle continua ativo.";
            Diag(LightRunHint.Text);
            if (_lightRunning) _lightTimer?.Start();
        }
    }

    // ---------------- Conexões ----------------

    private async Task ConnectAllAsync()
    {
        if (_lightBusy) return;
        if (_lightControlled.Count > 0) StopLighting(true);
        _lightBusy = true;
        BtnLightConnect.IsEnabled = false;
        try
        {
            await Task.WhenAll(ConnectCorsairAsync(), ConnectOrgbAsync(), ConnectRazerAsync());
            RebuildLightList();
        }
        catch (Exception ex) { Diag("Falha geral: " + ex.Message); }
        finally { _lightBusy = false; BtnLightConnect.IsEnabled = true; }
    }

    private void RebuildLightList()
    {
        var keep = LightProfiles.Active(_settings).Unselected.ToHashSet();
        _lightVms = _devsByBackend.Values.SelectMany(x => x)
            .Select(d => new LightDeviceVm { Device = d, Selected = !keep.Contains(d.Key) }).ToList();
        LightDevices.ItemsSource = _lightVms;
        LightDevicesHint.Text = _lightVms.Count == 0
            ? "Nenhum dispositivo encontrado ainda. Conecte pelo menos uma integração acima."
            : $"{_lightVms.Count} dispositivo(s), {_lightVms.Sum(v => v.Device.LedCount)} LEDs no total. Desmarque o que não quer controlar.";
        UpdateLightKeep();
    }

    private async Task ConnectCorsairAsync()
    {
        try
        {
            if (_cue == null)
            {
                _cue = new CorsairSdk();
                _corsair = new CorsairBackend(_cue);
                _cue.StateChanged += s => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (s != 6 && _lightRunning) { StopLighting(false); LightStatus.Text = "O iCUE desconectou (" + CorsairSdk.StateText(s) + "). Clique em CONECTAR TUDO."; }
                }));
            }
            _devsByBackend.Remove(_corsair!.Name);
            string? dllProblem = _cue.LoadDll();
            BtnLightSdk.Visibility = dllProblem != null ? Visibility.Visible : Visibility.Collapsed;
            if (dllProblem != null)
            {
                LightStatus.Text = "O SDK da Corsair (iCUESDK.x64_2019.dll) ainda não está instalado. Clique em BAIXAR O SDK DA CORSAIR: "
                                   + "o programa baixa só essa DLL do repositório oficial da Corsair e a instala para você.";
                return;
            }

            if (_cue.State != 6)
            {
                LightStatus.Text = "Conectando ao iCUE…";
                var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnState(int s) { if (s == 6 || s == 3 || s == 4) tcs.TrySetResult(s); }
                _cue.StateChanged += OnState;
                int err = _cue.Connect();
                if (err != 0 || _cue.State == 6) tcs.TrySetResult(_cue.State);
                await Task.WhenAny(tcs.Task, Task.Delay(6000));
                _cue.StateChanged -= OnState;
                if (_cue.State != 6)
                {
                    LightStatus.Text = "Não consegui conectar ao iCUE (" + (err != 0 ? CorsairSdk.ErrorText(err) : CorsairSdk.StateText(_cue.State)) + "). " +
                        "Abra o iCUE, vá em Configurações e ative o SDK (Enable SDK). Se o iCUE estiver em outra conta do Windows ou com permissão maior que a do ResetExplode, a conexão também falha.";
                    return;
                }
            }

            string? prob = null;
            var devs = await Task.Run(() => _cue.ReadDevices(out prob));
            if (prob != null) { LightStatus.Text = prob; return; }
            _devsByBackend[_corsair.Name] = _corsair.Wrap(devs);

            Diag($"iCUE · DLL: {_cue.LoadedFrom} · sessão: {CorsairSdk.StateText(_cue.State)} · {devs.Count} dispositivo(s)");
            foreach (var d in devs)
            {
                string idShort = d.Id.Length > 14 ? d.Id[..14] + "…" : d.Id;
                string first = d.LedCount > 0 ? $"LED[0]=#{d.LedIds[0]} x={d.LedX[0]:0.00}" : "sem LEDs lidos";
                first += " · " + d.LedReadInfo;
                Diag($"  {d.TypeName} \"{d.Model}\" id={idShort} leds(ficha)={d.InfoLedCount} leds(posições)={d.LedCount} canais={d.ChannelCount} {first}");
            }
            LightStatus.Text = devs.Count == 0
                ? "Conectado ao iCUE, mas nenhum dispositivo foi listado. Confira se o iCUE LINK System Hub aparece no iCUE."
                : $"Conectado ao iCUE · {devs.Count} dispositivo(s), {devs.Sum(d => d.LedCount)} LEDs.";
        }
        catch (Exception ex) { LightStatus.Text = "Falhou: " + ex.Message; }
    }

    private async Task ConnectOrgbAsync()
    {
        try
        {
            _orgb ??= new OpenRgbBackend();
            _devsByBackend.Remove(_orgb.Name);
            var (devs, status) = await Task.Run(() => _orgb.Connect("127.0.0.1", 6742));
            if (devs.Count > 0)
            {
                _devsByBackend[_orgb.Name] = devs;
                OrgbStatus.Text = "OpenRGB: " + status;
            }
            else
            {
                OrgbStatus.Text = "OpenRGB: " + status + " Ele cobre MSI Mystic Light, Gigabyte RGB Fusion, ASUS Aura e outras marcas: abra o OpenRGB e ligue o servidor " +
                                  "(aba SDK Server → Start Server), ou use o botão ao lado. Feche antes o MSI Center, o Armoury Crate e o RGB Fusion, que disputam os mesmos controladores.";
            }
            Diag("OpenRGB · " + status);
            foreach (var d in devs) Diag($"  {d.Name} · {d.Detail}");
        }
        catch (Exception ex) { OrgbStatus.Text = "OpenRGB: falhou: " + ex.Message; }
    }

    private async Task ConnectRazerAsync()
    {
        try
        {
            _razer ??= new RazerChromaBackend();
            _razer.EndSession();
            _devsByBackend.Remove(_razer.Name);
            var (devs, status) = await _razer.ConnectAsync();
            if (devs.Count > 0) _devsByBackend[_razer.Name] = devs;
            RazerStatus.Text = "Razer Chroma: " + status;
            Diag("Razer Chroma · " + status);
        }
        catch (Exception ex) { RazerStatus.Text = "Razer Chroma: falhou: " + ex.Message; }
    }

    private async void BtnLightConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_lightRunning) StopLighting(true);
        await ConnectAllAsync();
    }

    private static string? FindOpenRgb()
    {
        try
        {
            var dirs = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenRGB"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "OpenRGB"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenRGB"),
            };
            foreach (var baseDir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Downloads",
                                            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) })
            {
                if (!Directory.Exists(baseDir)) continue;
                foreach (var sub in Directory.GetDirectories(baseDir, "OpenRGB*"))
                {
                    dirs.Add(sub);
                    dirs.AddRange(Directory.GetDirectories(sub));
                }
            }
            foreach (var d in dirs)
            {
                string exe = Path.Combine(d, "OpenRGB.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        catch { }
        return null;
    }

    private async void BtnOrgbOpen_Click(object sender, RoutedEventArgs e)
    {
        string? exe = FindOpenRgb();
        if (exe == null)
        {
            try { Process.Start(new ProcessStartInfo("https://openrgb.org/") { UseShellExecute = true }); } catch { }
            OrgbStatus.Text = "OpenRGB: abri o site openrgb.org no navegador. Baixe, descompacte (pode ser em Downloads ou Documentos), e volte aqui.";
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(exe, "--server --startminimized") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
            OrgbStatus.Text = "OpenRGB: abrindo o servidor…";
            await Task.Delay(4000);
            await ConnectAllAsync();
        }
        catch (Exception ex) { OrgbStatus.Text = "OpenRGB: não consegui abrir: " + ex.Message; }
    }

    private async void BtnLightSdk_Click(object sender, RoutedEventArgs e)
    {
        if (_lightBusy) return;
        var ok = MessageBox.Show(this,
            "O RESET EXPLODE vai baixar o SDK oficial da Corsair (github.com/CorsairOfficial/cue-sdk), extrair somente o arquivo " +
            "iCUESDK.x64_2019.dll para a pasta\n" + CorsairSdkInstaller.TargetPath + "\ne conferir a assinatura digital dele.\n\nContinuar?",
            "RESET EXPLODE · SDK da Corsair", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) return;

        _lightBusy = true;
        BtnLightSdk.IsEnabled = false;
        BtnLightConnect.IsEnabled = false;
        try
        {
            string msg = await CorsairSdkInstaller.InstallAsync(t => Dispatcher.BeginInvoke(new Action(() => LightStatus.Text = t)));
            LightStatus.Text = msg;
            BtnLightSdk.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { LightStatus.Text = "Não foi possível instalar o SDK: " + ex.Message; return; }
        finally
        {
            _lightBusy = false;
            BtnLightSdk.IsEnabled = true;
            BtnLightConnect.IsEnabled = true;
        }
        await ConnectAllAsync();
    }

    // ---------------- Controle ----------------

    private void BtnLightRun_Click(object sender, RoutedEventArgs e)
    {
        if (_lightRunning) { StopLighting(true); return; }
        StartLighting();
    }

    private void StartLighting()
    {
        // Pode haver uma sessão interrompida por desconexão do iCUE.
        if (_lightControlled.Count > 0) StopLighting(true);
        var sel = _lightVms.Where(v => v.Selected && v.Device.LedCount > 0).ToList();
        if (sel.Count == 0) { LightRunHint.Text = "Conecte e marque pelo menos um dispositivo com LEDs."; return; }
        LightRunHint.Text = "";
        LightOpenRgbGuide.Visibility = Visibility.Collapsed;

        var problems = new List<string>();
        bool exclusive = LightExclusive.IsChecked == true;
        if (_cue != null && _cue.State == 6 && sel.Any(v => v.Device.Backend == _corsair))
        {
            int pr = _cue.SetLayerPriority(255);
            Diag($"iCUE · prioridade da camada 255: {(pr == -1 ? "função ausente nesta versão do SDK" : CorsairSdk.ErrorText(pr))} · modo {(exclusive ? "exclusivo" : "compartilhado")}");
        }
        _lightErrByDevice.Clear();
        foreach (var v in sel)
        {
            int err = v.Device.Backend.Acquire(v.Device, exclusive);
            Diag($"Pedido de controle de \"{v.Name}\" ({v.Device.Backend.Name}): {v.Device.Backend.ErrorText(err)}");
            if (err != 0) problems.Add($"{v.Name}: {v.Device.Backend.ErrorText(err)}");
            else _lightControlled[v.Device.Key] = v.Device;
        }
        _lightLast.Clear();
        _smoothValue = float.NaN;
        _lightRunning = true;
        BtnLightRun.Content = "DEVOLVER AO ORIGINAL";
        LightRunHint.Text = problems.Count == 0
            ? $"Controlando {sel.Count} dispositivo(s)."
            : "Controle ativo, mas com avisos: " + string.Join("; ", problems);
        _lightTimer!.Start();
        UpdateLightKeep();
        LightTick(null, EventArgs.Empty);
    }

    private void StopLighting(bool release, bool keepOpenRgb = false)
    {
        _lightTimer?.Stop();
        _lightRunning = false;
        BtnLightRun.Content = "ATIVAR CONTROLE";
        LightLive.Text = "";
        UpdateLightKeep();
        if (!release) return;
        var backends = new HashSet<ILightBackend>();
        foreach (var d in _lightControlled.Values)
        {
            try
            {
                if (keepOpenRgb && d.Backend is OpenRgbBackend orgb) orgb.Keep(d);
                else d.Backend.Release(d);
                backends.Add(d.Backend);
            }
            catch { }
        }
        _lightControlled.Clear();
        foreach (var b in backends) { try { b.EndSession(); } catch { } }
        LightRunHint.Text = keepOpenRgb
            ? "Cores deixadas no OpenRGB. O iCUE e o Synapse reassumem os demais dispositivos."
            : "Controle devolvido: o iCUE, o OpenRGB e o Synapse reassumem as luzes.";
    }

    private void ShutdownInteg()
    {
        try { StopLighting(true); } catch { }
        try { _cue?.Dispose(); } catch { }
        try { _orgb?.Shutdown(); } catch { }
        try { _razer?.Shutdown(); } catch { }
    }

    private void LightFx_Checked(object sender, RoutedEventArgs e)
    {
        if (LightLive == null) return;
        _effect = (string)((FrameworkElement)sender).Tag;
        _smoothValue = float.NaN;
        _lightLast.Clear();
        SaveLightProfile();
        UpdateLightKeep();
        if (_lightRunning && !_lightLoading) LightTick(null, EventArgs.Empty);
    }

    private void LightColor_Changed(object? sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LightLive == null) return;
        _lightColor = ((byte)LightR.Value, (byte)LightG.Value, (byte)LightB.Value);
        LightPreview.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(_lightColor.R, _lightColor.G, _lightColor.B));
        LightBrightText.Text = $"{LightBright.Value:0}%";
        LightSpeedText.Text = $"{LightSpeed.Value:0}";
        _lightLast.Clear();
        SaveLightProfile();
    }

    // ---------------- Efeitos ----------------

    private static (byte, byte, byte) Hsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
            < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x)
        };
        return ((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    private float? GpuTemperature()
    {
        var r = _monitor.Rows.Where(x => (x.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                                         && x.Type == SensorType.Temperature && x.RawValue is > 0).ToList();
        return (r.FirstOrDefault(x => x.Name.Contains("Core")) ?? r.FirstOrDefault())?.RawValue;
    }

    /// <summary>Valor ao vivo que dirige o efeito (temperatura ou carga), já suavizado. Null se o sensor não responde.</summary>
    private float? LiveValue()
    {
        float? raw = _effect switch
        {
            "cpu" => _monitor.CpuTemperature,
            "gpu" => GpuTemperature(),
            "load" => _monitor.CpuLoad,
            _ => null
        };
        if (raw == null) return null;
        _smoothValue = float.IsNaN(_smoothValue) ? raw.Value : _smoothValue + (raw.Value - _smoothValue) * 0.08f;
        return _smoothValue;
    }

    private void LightTick(object? sender, EventArgs e)
    {
        double t = _lightClock.Elapsed.TotalSeconds;
        double bright = LightBright.Value / 100.0;
        double speed = LightSpeed.Value;
        var (cr, cg, cb) = _lightColor;

        (byte, byte, byte)? live = null;
        if (_effect is "cpu" or "gpu" or "load")
        {
            float? v = LiveValue();
            if (v == null) { LightLive.Text = "Sensor indisponível."; live = (0, 0, 40); }
            else
            {
                double hue;
                if (_effect == "load") { hue = 120 * (1 - Math.Clamp(v.Value / 100.0, 0, 1)); LightLive.Text = $"CPU {v:0}% de carga"; }
                else { hue = 240 * (1 - Math.Clamp((v.Value - 35) / 50.0, 0, 1)); LightLive.Text = $"{(_effect == "cpu" ? "CPU" : "GPU")} {v:0} °C"; }
                live = Hsv(hue, 1, 1);
            }
        }
        else LightLive.Text = "";

        long now = Environment.TickCount64;
        foreach (var vm in _lightVms)
        {
            if (!vm.Selected || vm.Device.LedCount == 0 || !_lightControlled.ContainsKey(vm.Device.Key)) continue;
            var d = vm.Device;
            if (d.Backend == _corsair && (_cue == null || _cue.State != 6)) continue;

            bool have = _lightLast.TryGetValue(d.Key, out var last);
            if (have && now - last.At < d.Backend.MinIntervalMs) continue;

            var cols = new (byte, byte, byte)[d.LedCount];
            for (int i = 0; i < cols.Length; i++)
            {
                (byte R, byte G, byte B) c;
                switch (_effect)
                {
                    case "breath":
                        double k = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (0.1 * speed) * t);
                        k = 0.04 + 0.96 * k;
                        c = ((byte)(cr * k), (byte)(cg * k), (byte)(cb * k));
                        break;
                    case "rainbow":
                        c = Hsv(d.LedX[i] * 360 + t * speed * 36, 1, 1);
                        break;
                    case "cpu": case "gpu": case "load":
                        c = live ?? (0, 0, 0);
                        break;
                    case "off":
                        c = (0, 0, 0);
                        break;
                    default:
                        c = (cr, cg, cb);
                        break;
                }
                cols[i] = ((byte)(c.R * bright), (byte)(c.G * bright), (byte)(c.B * bright));
            }

            // só reenvia se mudou (ou a cada 2 s, para o fabricante não retomar as luzes)
            if (have && now - last.At < 2000 && last.Colors.AsSpan().SequenceEqual(cols)) continue;
            int err = d.Backend.Send(d, cols);
            _lightLast[d.Key] = (cols, now);
            if (!_lightErrByDevice.TryGetValue(d.Key, out int prevErr) || prevErr != err)
            {
                _lightErrByDevice[d.Key] = err;
                Diag($"Cores enviadas a \"{vm.Name}\" ({d.Backend.Name}, {cols.Length} LEDs, primeiro = {cols[0].Item1},{cols[0].Item2},{cols[0].Item3}): {d.Backend.ErrorText(err)}");
            }
            if (err != 0 && err != _lastLightError) LightRunHint.Text = $"{vm.Name}: {d.Backend.ErrorText(err)}";
            _lastLightError = err;
        }
    }
}
