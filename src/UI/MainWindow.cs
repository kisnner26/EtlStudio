using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EtlStudio.Core;

namespace EtlStudio.UI
{
    /// <summary>Ventana principal (WPF construida en codigo, sin XAML, para compilar con MSBuild clasico).</summary>
    public class MainWindow : Window
    {
        readonly AppSettings settings = AppSettings.Load();
        readonly List<ClaudeClient.Turn> chat = new List<ClaudeClient.Turn>();
        string lastVerification = "";

        TabControl tabs;
        TextBlock status;
        // Conexiones
        TextBox txtServer; ComboBox cmbDsn, cmbModel; PasswordBox pwdKey; TextBlock keyState;
        // Fuente / diseno
        TextBox txtQuery, txtSpec, txtHint; DataGrid gridPreview; DataTable lastPreview;
        // Ejecutar
        CheckBox chkRecreate; TextBox txtOut, txtLog;
        // Verificar
        StackPanel verifyPanel;
        // Asistente
        TextBox txtChat, txtAsk;

        static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x1F, 0x4E, 0x79));
        static readonly FontFamily Mono = new FontFamily("Consolas");

        public MainWindow()
        {
            Title = "EtlStudio - DW + SSIS con Claude";
            Width = 1150; Height = 780; MinWidth = 900; MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var root = new DockPanel();
            var header = new Border { Background = Accent, Padding = new Thickness(16, 10, 16, 10) };
            var hp = new StackPanel();
            hp.Children.Add(new TextBlock { Text = "EtlStudio", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Brushes.White });
            hp.Children.Add(new TextBlock { Text = "Origen Databricks/ODBC  →  Data Warehouse en SQL Server  +  paquete SSIS, asistido por Claude", Foreground = Brushes.WhiteSmoke });
            header.Child = hp; DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);

            status = new TextBlock { Text = "Listo.", Margin = new Thickness(10, 4, 10, 4) };
            var sb = new Border { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(0, 1, 0, 0), Child = status };
            DockPanel.SetDock(sb, Dock.Bottom); root.Children.Add(sb);

            tabs = new TabControl { Margin = new Thickness(8) };
            tabs.Items.Add(Tab("1 · Conexiones", BuildConnections()));
            tabs.Items.Add(Tab("2 · Fuente", BuildSource()));
            tabs.Items.Add(Tab("3 · Diseño (IA)", BuildDesign()));
            tabs.Items.Add(Tab("4 · Ejecutar", BuildRun()));
            tabs.Items.Add(Tab("5 · Verificar", BuildVerify()));
            tabs.Items.Add(Tab("Asistente Claude", BuildChat()));
            root.Children.Add(tabs);
            Content = root;

            txtServer.Text = settings.SqlServer;
            RefreshDsns(); cmbDsn.Text = settings.Dsn;
            cmbModel.ItemsSource = new[] { "claude-sonnet-5", "claude-opus-5-5", "claude-fable-5-1", "claude-haiku-4-5-20251001" };
            cmbModel.Text = string.IsNullOrEmpty(settings.Model) ? "claude-sonnet-5" : settings.Model;
            UpdateKeyState();
            LoadExample();
        }

        // ---------------- helpers UI ----------------
        static TabItem Tab(string title, UIElement content) { return new TabItem { Header = title, Content = content, FontSize = 13 }; }
        static TextBlock Lbl(string t) { return new TextBlock { Text = t, Margin = new Thickness(0, 8, 0, 2), FontWeight = FontWeights.SemiBold }; }
        static TextBlock Note(string t) { return new TextBlock { Text = t, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) }; }
        static Button Btn(string t, RoutedEventHandler h) { var b = new Button { Content = t, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) }; b.Click += h; return b; }
        static StackPanel Row(params UIElement[] c) { var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) }; foreach (var x in c) p.Children.Add(x); return p; }
        static TextBox Box(bool multi = false, bool mono = false)
        {
            var t = new TextBox { Padding = new Thickness(4), TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap };
            if (multi) { t.AcceptsReturn = true; t.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; }
            if (mono) t.FontFamily = Mono;
            return t;
        }
        void SetStatus(string s) { Dispatcher.Invoke(() => status.Text = s); }
        void Log(string s) { Dispatcher.Invoke(() => { txtLog.AppendText(s + Environment.NewLine); txtLog.ScrollToEnd(); }); }
        string ServerName { get { return txtServer.Text.Trim(); } }
        string DsnName { get { return cmbDsn.Text.Trim(); } }

        /// <summary>Ejecuta trabajo pesado fuera del hilo de UI y muestra errores sin cerrar la app.</summary>
        async void Work(string title, Action job, bool toLog = false)
        {
            SetStatus(title + "...");
            if (toLog) Log("▶ " + title);
            try { await Task.Run(job); SetStatus(title + ": OK"); if (toLog) Log("✔ " + title + ": OK"); }
            catch (Exception ex)
            {
                var m = ex.InnerException != null && ex is AggregateException ? ex.InnerException.Message : ex.Message;
                SetStatus(title + ": ERROR");
                if (toLog) Log("✖ " + title + ": " + m); else MessageBox.Show(this, m, title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ---------------- 1. Conexiones ----------------
        UIElement BuildConnections()
        {
            var p = new StackPanel { Margin = new Thickness(14) };
            p.Children.Add(Lbl("Instancia de SQL Server (autenticacion de Windows)"));
            txtServer = Box(); p.Children.Add(txtServer);
            p.Children.Add(Row(Btn("Probar SQL Server", (s, e) => { var sv = ServerName; Work("Probar SQL Server", () => Say(SqlRunner.Test(sv))); })));

            p.Children.Add(Lbl("Origen: DSN ODBC (por ejemplo el DSN de Databricks; el token queda en el DSN, no en esta app)"));
            cmbDsn = new ComboBox { IsEditable = true }; p.Children.Add(cmbDsn);
            p.Children.Add(Row(
                Btn("Actualizar lista", (s, e) => RefreshDsns()),
                Btn("Probar ODBC", (s, e) => { var d = DsnName; Work("Probar ODBC", () => Say(SourceReader.Test(d))); }),
                Btn("Abrir administrador ODBC (64 bits)", (s, e) => System.Diagnostics.Process.Start(Path.Combine(Environment.SystemDirectory, "odbcad32.exe")))));

            p.Children.Add(Lbl("Claude (API de Anthropic, de pago)"));
            p.Children.Add(Note("Tu API key se guarda cifrada con DPAPI (solo tu usuario de Windows la puede leer) en %APPDATA%\\EtlStudio. Tambien se lee de la variable de entorno ANTHROPIC_API_KEY. Nunca se sube al repositorio."));
            pwdKey = new PasswordBox { Padding = new Thickness(4) }; p.Children.Add(pwdKey);
            cmbModel = new ComboBox { IsEditable = true, Margin = new Thickness(0, 6, 0, 0) }; p.Children.Add(cmbModel);
            keyState = new TextBlock { Margin = new Thickness(0, 6, 0, 0) }; p.Children.Add(keyState);
            p.Children.Add(Row(Btn("Guardar ajustes", (s, e) => SaveSettings())));
            return new ScrollViewer { Content = p };
        }

        void Say(string m) { Dispatcher.Invoke(() => MessageBox.Show(this, m, "EtlStudio", MessageBoxButton.OK, MessageBoxImage.Information)); }

        void RefreshDsns() { cmbDsn.ItemsSource = SourceReader.ListDsns(); }

        void SaveSettings()
        {
            settings.SqlServer = ServerName; settings.Dsn = DsnName; settings.Model = cmbModel.Text.Trim();
            if (pwdKey.Password.Length > 0) { settings.SetApiKey(pwdKey.Password); pwdKey.Clear(); }
            settings.Save(); UpdateKeyState(); SetStatus("Ajustes guardados.");
        }

        void UpdateKeyState()
        {
            bool ok = !string.IsNullOrEmpty(settings.GetApiKey());
            keyState.Text = ok ? "API key: configurada ✔" : "API key: NO configurada";
            keyState.Foreground = ok ? Brushes.DarkGreen : Brushes.Firebrick;
        }

        ClaudeClient Claude()
        {
            var key = settings.GetApiKey();
            if (string.IsNullOrEmpty(key)) throw new InvalidOperationException("Configura tu API key de Anthropic en la pestana 'Conexiones'.");
            return new ClaudeClient(key, string.IsNullOrEmpty(settings.Model) ? "claude-sonnet-5" : settings.Model);
        }

        // ---------------- 2. Fuente ----------------
        UIElement BuildSource()
        {
            var g = new Grid { Margin = new Thickness(14) };
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(150) });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var l = Lbl("SELECT de origen (lo que vas a llevar al DW)"); Grid.SetRow(l, 0); g.Children.Add(l);
            txtQuery = Box(true, true); Grid.SetRow(txtQuery, 1); g.Children.Add(txtQuery);
            var r = Row(Btn("Vista previa (20 filas)", (s, e) => Preview())); Grid.SetRow(r, 2); g.Children.Add(r);
            gridPreview = new DataGrid { IsReadOnly = true, Margin = new Thickness(0, 8, 0, 0), AutoGenerateColumns = true }; Grid.SetRow(gridPreview, 3); g.Children.Add(gridPreview);
            return g;
        }

        void Preview()
        {
            string dsn = DsnName, q = txtQuery.Text.Trim();
            Work("Vista previa", () =>
            {
                var dt = SourceReader.Preview(dsn, q, 20);
                lastPreview = dt;
                Dispatcher.Invoke(() => gridPreview.ItemsSource = dt.DefaultView);
            });
        }

        // ---------------- 3. Diseno ----------------
        UIElement BuildDesign()
        {
            var g = new Grid { Margin = new Thickness(14) };
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var top = new StackPanel();
            top.Children.Add(Note("Aqui defines el modelo en estrella (dimensiones, hechos, medidas). Claude puede proponerlo a partir del esquema de la pestana Fuente; siempre lo revisas antes de crear nada."));
            top.Children.Add(Lbl("Indicaciones para Claude (opcional)"));
            txtHint = Box(); top.Children.Add(txtHint);
            Grid.SetRow(top, 0); g.Children.Add(top);
            var r = Row(
                Btn("Proponer DW con Claude", (s, e) => ProposeWithClaude()),
                Btn("Ejemplo: Autos", (s, e) => LoadExample()),
                Btn("Validar", (s, e) => ValidateSpec()),
                Btn("Abrir...", (s, e) => OpenSpec()),
                Btn("Guardar...", (s, e) => SaveSpec()));
            Grid.SetRow(r, 1); g.Children.Add(r);
            txtSpec = Box(true, true); txtSpec.Margin = new Thickness(0, 8, 0, 0); Grid.SetRow(txtSpec, 2); g.Children.Add(txtSpec);
            return g;
        }

        void LoadExample()
        {
            var s = DwSpec.ExampleAutos(); s.SqlServer = string.IsNullOrEmpty(ServerName) ? s.SqlServer : ServerName;
            if (!string.IsNullOrEmpty(DsnName)) s.SourceDsn = DsnName;
            txtSpec.Text = s.ToJson(); txtQuery.Text = s.SourceQuery;
        }

        DwSpec ReadSpec()
        {
            DwSpec s;
            try { s = DwSpec.FromJson(txtSpec.Text); } catch (Exception ex) { throw new InvalidOperationException("El JSON del diseno no es valido: " + ex.Message); }
            var errs = s.Validate();
            if (errs.Count > 0) throw new InvalidOperationException("El diseno tiene problemas:\n - " + string.Join("\n - ", errs));
            return s;
        }

        void ValidateSpec()
        {
            try { var s = ReadSpec(); SetStatus("Diseno valido: " + s.Dims.Count + " dimensiones, medidas: " + s.Fact.Measures.Count + "."); MessageBox.Show(this, "El diseno es valido.", "Validar"); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Validar", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        void OpenSpec()
        {
            var d = new Microsoft.Win32.OpenFileDialog { Filter = "Diseno JSON|*.json" };
            if (d.ShowDialog() == true) txtSpec.Text = File.ReadAllText(d.FileName);
        }

        void SaveSpec()
        {
            var d = new Microsoft.Win32.SaveFileDialog { Filter = "Diseno JSON|*.json", FileName = "dw_spec.json" };
            if (d.ShowDialog() == true) File.WriteAllText(d.FileName, txtSpec.Text);
        }

        void ProposeWithClaude()
        {
            string dsn = DsnName, server = ServerName, q = txtQuery.Text.Trim(), hint = txtHint.Text.Trim();
            Work("Proponer DW con Claude", () =>
            {
                var claude = Claude();
                var dt = lastPreview != null ? lastPreview : SourceReader.Preview(dsn, q, 20);
                var msg = "SELECT de origen:\n" + q + "\n\nEsquema:\n" + SourceReader.DescribeSchema(dt) + "\nFilas de muestra:\n" + SourceReader.SampleRows(dt, 8) +
                          (hint.Length > 0 ? "\nIndicaciones del usuario: " + hint : "");
                var reply = claude.Send(ClaudeClient.DesignSystemPrompt, new[] { new ClaudeClient.Turn { Role = "user", Text = msg } });
                var spec = DwSpec.FromJson(ClaudeClient.ExtractJson(reply));
                spec.SqlServer = server; spec.SourceDsn = dsn; spec.SourceQuery = q;   // datos de conexion siempre los pone el usuario
                var errs = spec.Validate();
                Dispatcher.Invoke(() =>
                {
                    txtSpec.Text = spec.ToJson();
                    if (errs.Count > 0) MessageBox.Show(this, "Claude propuso un diseno con problemas; corrigelo o vuelve a pedirlo:\n - " + string.Join("\n - ", errs), "Diseno", MessageBoxButton.OK, MessageBoxImage.Warning);
                });
            });
        }

        // ---------------- 4. Ejecutar ----------------
        UIElement BuildRun()
        {
            var g = new Grid { Margin = new Thickness(14) };
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var top = new StackPanel();
            top.Children.Add(Lbl("Archivo del paquete SSIS (.dtsx)"));
            txtOut = Box(); txtOut.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EtlStudio", "ETL.dtsx"); top.Children.Add(txtOut);
            chkRecreate = new CheckBox { Content = "Recrear la base de datos (BORRA la base existente y todos sus datos)", Foreground = Brushes.Firebrick, Margin = new Thickness(0, 8, 0, 0) };
            top.Children.Add(chkRecreate);
            top.Children.Add(Row(
                Btn("1. Crear DW", (s, e) => CreateDw()),
                Btn("2. Generar paquete", (s, e) => GeneratePackage()),
                Btn("3. Validar", (s, e) => RunPackage(true)),
                Btn("4. Ejecutar carga", (s, e) => RunPackage(false)),
                Btn("Todo en uno", (s, e) => AllInOne())));
            top.Children.Add(Row(Btn("Ver DDL", (s, e) => ShowDdl()), Btn("Abrir carpeta", (s, e) => OpenFolder())));
            Grid.SetRow(top, 0); g.Children.Add(top);
            txtLog = Box(true, true); txtLog.IsReadOnly = true; txtLog.Margin = new Thickness(0, 10, 0, 0); txtLog.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)); txtLog.Foreground = Brushes.Gainsboro;
            Grid.SetRow(txtLog, 1); g.Children.Add(txtLog);
            return g;
        }

        void OpenFolder() { var d = Path.GetDirectoryName(txtOut.Text.Trim()); Directory.CreateDirectory(d); System.Diagnostics.Process.Start(d); }

        void ShowDdl()
        {
            try
            {
                var s = ReadSpec();
                new Window { Title = "DDL generado", Width = 900, Height = 650, Owner = this, Content = new TextBox { Text = DdlGenerator.Tables(s), IsReadOnly = true, FontFamily = Mono, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto } }.Show();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "DDL", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        void CreateDw()
        {
            DwSpec s; try { s = ReadSpec(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Diseno", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            bool recreate = chkRecreate.IsChecked == true;
            if (recreate && MessageBox.Show(this, "Se BORRARA la base '" + s.DwName + "' con todos sus datos. Continuar?", "Confirmar", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            Work("Crear DW", () => CreateDwCore(s, recreate), true);
        }

        void CreateDwCore(DwSpec s, bool recreate)
        {
            if (recreate) SqlRunner.Exec(s.SqlServer, "master", DdlGenerator.DropDatabase(s));
            SqlRunner.Exec(s.SqlServer, "master", DdlGenerator.CreateDatabase(s));
            SqlRunner.Exec(s.SqlServer, s.DwName, DdlGenerator.Tables(s));
            Log("   Base '" + s.DwName + "' y tablas listas en " + s.SqlServer + ".");
        }

        void GeneratePackage()
        {
            DwSpec s; try { s = ReadSpec(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Diseno", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            string path = txtOut.Text.Trim();
            Work("Generar paquete", () => GenerateCore(s, path), true);
        }

        void GenerateCore(DwSpec s, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            new SsisBuilder(s).Build(path);
            Log("   Paquete guardado en " + path + " (" + new FileInfo(path).Length / 1024 + " KB). Puedes agregarlo a Visual Studio con 'Add Existing Package'.");
        }

        void RunPackage(bool validateOnly)
        {
            string path = txtOut.Text.Trim();
            if (!File.Exists(path)) { MessageBox.Show(this, "Primero genera el paquete.", "Paquete", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            Work(validateOnly ? "Validar paquete" : "Ejecutar carga", () => RunCore(path, validateOnly), true);
        }

        void RunCore(string path, bool validateOnly)
        {
            var lines = new List<string>();
            int code = DtExec.Run(path, validateOnly, l => { lines.Add(l); if (l.Trim().Length > 0 && !l.StartsWith("Copyright")) Log("   " + l); });
            if (code != 0) throw new Exception("dtexec devolvio el codigo " + code + ". Revisa el log o pidele a Claude que lo explique (pestana Asistente).");
        }

        void AllInOne()
        {
            DwSpec s; try { s = ReadSpec(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Diseno", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            bool recreate = chkRecreate.IsChecked == true; string path = txtOut.Text.Trim();
            if (recreate && MessageBox.Show(this, "Se BORRARA la base '" + s.DwName + "' con todos sus datos. Continuar?", "Confirmar", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            Work("Proceso completo", () =>
            {
                Log("▶ 1/4 Crear DW"); CreateDwCore(s, recreate);
                Log("▶ 2/4 Generar paquete"); GenerateCore(s, path);
                Log("▶ 3/4 Validar"); RunCore(path, true);
                Log("▶ 4/4 Ejecutar carga (puede tardar varios minutos)"); RunCore(path, false);
                Log("✔ Listo. Ve a la pestana 'Verificar'.");
            }, true);
        }

        // ---------------- 5. Verificar ----------------
        UIElement BuildVerify()
        {
            var g = new Grid { Margin = new Thickness(14) };
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var r = Row(Btn("Ejecutar verificaciones", (s, e) => RunVerification()), Btn("Explicar resultados con Claude", (s, e) => ExplainVerification()));
            Grid.SetRow(r, 0); g.Children.Add(r);
            verifyPanel = new StackPanel();
            var sv = new ScrollViewer { Content = verifyPanel, Margin = new Thickness(0, 8, 0, 0) }; Grid.SetRow(sv, 1); g.Children.Add(sv);
            return g;
        }

        void RunVerification()
        {
            DwSpec s; try { s = ReadSpec(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Diseno", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            verifyPanel.Children.Clear(); var report = new StringBuilder();
            Work("Verificar", () =>
            {
                foreach (var c in Verification.For(s))
                {
                    DataTable dt = SqlRunner.Query(s.SqlServer, s.DwName, c.Sql);
                    report.AppendLine("## " + c.Title).AppendLine(SourceReader.SampleRows(dt, 12));
                    Dispatcher.Invoke(() =>
                    {
                        verifyPanel.Children.Add(new TextBlock { Text = c.Title, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 12, 0, 0) });
                        verifyPanel.Children.Add(Note(c.Explain));
                        verifyPanel.Children.Add(new TextBox { Text = c.Sql, IsReadOnly = true, FontFamily = Mono, Background = Brushes.WhiteSmoke, Padding = new Thickness(4) });
                        verifyPanel.Children.Add(new DataGrid { ItemsSource = dt.DefaultView, IsReadOnly = true, MaxHeight = 220, Margin = new Thickness(0, 4, 0, 0) });
                    });
                }
                lastVerification = report.ToString();
            });
        }

        void ExplainVerification()
        {
            if (lastVerification.Length == 0) { MessageBox.Show(this, "Primero ejecuta las verificaciones.", "Verificar"); return; }
            tabs.SelectedIndex = 5;
            AskClaude("Explica estos resultados de verificacion del DW y dime si algo se ve mal:\n" + lastVerification);
        }

        // ---------------- Asistente ----------------
        UIElement BuildChat()
        {
            var g = new Grid { Margin = new Thickness(14) };
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            txtChat = Box(true); txtChat.IsReadOnly = true; Grid.SetRow(txtChat, 0); g.Children.Add(txtChat);
            var p = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var send = Btn("Enviar", (s, e) => { var q = txtAsk.Text.Trim(); if (q.Length > 0) { txtAsk.Clear(); AskClaude(q); } }); DockPanel.SetDock(send, Dock.Right);
            p.Children.Add(send);
            txtAsk = Box(); txtAsk.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) { var q = txtAsk.Text.Trim(); if (q.Length > 0) { txtAsk.Clear(); AskClaude(q); } } };
            p.Children.Add(txtAsk); Grid.SetRow(p, 1); g.Children.Add(p);
            return g;
        }

        void AskClaude(string question)
        {
            string spec = txtSpec.Text; string log = string.Join("\n", txtLog.Text.Split('\n').Reverse().Take(60).Reverse());
            chat.Add(new ClaudeClient.Turn { Role = "user", Text = question });
            txtChat.AppendText("Tú: " + question + Environment.NewLine + Environment.NewLine);
            var snapshot = chat.ToList();
            Work("Consultar a Claude", () =>
            {
                var sys = ClaudeClient.ChatSystemPrompt + "\n\nDiseno actual (JSON):\n" + spec + "\n\nUltimas lineas del log:\n" + log;
                var reply = Claude().Send(sys, snapshot);
                chat.Add(new ClaudeClient.Turn { Role = "assistant", Text = reply });
                Dispatcher.Invoke(() => { txtChat.AppendText("Claude: " + reply + Environment.NewLine + Environment.NewLine); txtChat.ScrollToEnd(); });
            });
        }
    }
}
