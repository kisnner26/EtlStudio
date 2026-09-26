using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using EtlStudio.Core;

namespace EtlStudio.UI
{
    /// <summary>Ventana principal: WPF construida en codigo (sin XAML) para compilar con MSBuild clasico.</summary>
    public class MainWindow : Window
    {
        readonly AppSettings settings = AppSettings.Load();
        readonly List<ClaudeClient.Turn> chat = new List<ClaudeClient.Turn>();
        readonly List<Attachment> attachments = new List<Attachment>();
        string lastVerification = "";
        bool busy;

        ContentControl host; UIElement[] pages; RadioButton[] navButtons;
        TextBlock status;
        // Ajustes
        PasswordBox pwdKey; ComboBox cmbModel; TextBlock keyState;
        // Entorno (compartido)
        ComboBox cmbServer, cmbDsn;
        // Inicio
        TextBox txtPrompt; TextBlock promptHint; WrapPanel chips; Rectangle dropRect;
        CheckBox chkReview, chkRecreateHome;
        Border cardProgress, cardPlan, cardResult, cardError;
        ProgressBar bar; TextBlock progressTitle, progressDetail, planTitle, planBody, resultTitle, resultBody, errorBody;
        StackPanel stepsPanel; Dictionary<string, TextBlock> stepGlyph = new Dictionary<string, TextBlock>();
        Button btnOpenOdbc; TaskCompletionSource<bool> approval; PipelineResult lastResult;
        // Fuente / diseno / ejecutar / verificar / chat
        TextBox txtQuery, txtSpec, txtHint, txtOut, txtLog, txtChat, txtAsk; DataGrid gridPreview; DataTable lastPreview;
        CheckBox chkRecreate; StackPanel verifyPanel;

        static readonly FontFamily Mono = new FontFamily("Cascadia Mono, Consolas");

        public MainWindow()
        {
            Title = "EtlStudio";
            Width = 1240; Height = 820; MinWidth = 1000; MinHeight = 640;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Theme.Apply(this);
            try { Icon = BitmapFrame.Create(Assembly.GetExecutingAssembly().GetManifestResourceStream("EtlStudio.app.ico")); } catch { }

            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(236) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // ---- Barra lateral ----
            var side = new Border { Background = Theme.B(Color.FromRgb(0xEC, 0xEC, 0xF1)), BorderBrush = Theme.B(Theme.Line), BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(14, 18, 14, 14) };
            var sp = new DockPanel();
            var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 22) };
            brand.Children.Add(LogoMark(38));
            var bt = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            bt.Children.Add(new TextBlock { Text = "EtlStudio", FontSize = 18, FontWeight = FontWeights.Bold, FontFamily = Theme.FontDisplay });
            bt.Children.Add(new TextBlock { Text = "DW · SSIS · Claude", FontSize = 11, Foreground = Theme.B(Theme.Muted) });
            brand.Children.Add(bt); DockPanel.SetDock(brand, Dock.Top); sp.Children.Add(brand);

            var foot = new TextBlock { Text = "v1.1", Foreground = Theme.B(Theme.Muted), FontSize = 11, Margin = new Thickness(8, 8, 0, 0) };
            DockPanel.SetDock(foot, Dock.Bottom); sp.Children.Add(foot);
            var navBottom = new StackPanel(); DockPanel.SetDock(navBottom, Dock.Bottom);
            var navTop = new StackPanel();

            navButtons = new RadioButton[6];
            navButtons[0] = Nav("", "Inicio", 0); navTop.Children.Add(navButtons[0]);
            navTop.Children.Add(new TextBlock { Text = "AVANZADO", FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.B(Theme.Muted), Margin = new Thickness(12, 18, 0, 4) });
            navButtons[1] = Nav("", "Diseño", 1); navTop.Children.Add(navButtons[1]);
            navButtons[2] = Nav("", "Ejecutar", 2); navTop.Children.Add(navButtons[2]);
            navButtons[3] = Nav("", "Verificar", 3); navTop.Children.Add(navButtons[3]);
            navButtons[4] = Nav("", "Asistente", 4); navTop.Children.Add(navButtons[4]);
            navButtons[5] = Nav("", "Ajustes", 5); navBottom.Children.Add(navButtons[5]);
            sp.Children.Add(navBottom); sp.Children.Add(navTop);
            side.Child = sp; Grid.SetColumn(side, 0); root.Children.Add(side);

            // ---- Contenido ----
            var right = new DockPanel();
            status = new TextBlock { Text = "Listo.", Margin = new Thickness(20, 6, 20, 6), Foreground = Theme.B(Theme.Muted), FontSize = 12 };
            var sb = new Border { BorderBrush = Theme.B(Theme.Line), BorderThickness = new Thickness(0, 1, 0, 0), Child = status };
            DockPanel.SetDock(sb, Dock.Bottom); right.Children.Add(sb);
            host = new ContentControl(); right.Children.Add(host);
            Grid.SetColumn(right, 1); root.Children.Add(right);
            Content = root;

            cmbServer = new ComboBox { IsEditable = true }; cmbDsn = new ComboBox { IsEditable = true };
            pages = new UIElement[]
            {
                BuildHome(),
                Page("Diseño", "Define el modelo en estrella. Claude puede proponerlo; siempre lo revisas antes de crear nada.", BuildDesign()),
                Page("Ejecutar", "Crea el DW, genera el paquete SSIS, valida y carga. Cada paso por separado o todo en uno.", BuildRun()),
                Page("Verificar", "Consultas de solo lectura y comentadas para comprobar la carga.", BuildVerify()),
                Page("Asistente", "Pregunta a Claude sobre tu diseño, los resultados o cualquier error.", BuildChat()),
                Page("Ajustes", "Conexiones y tu API key de Anthropic.", BuildSettings())
            };

            LoadEnvironment();
            cmbModel.ItemsSource = new[] { "claude-sonnet-5", "claude-opus-5-5", "claude-fable-5-1", "claude-haiku-4-5-20251001" };
            cmbModel.Text = string.IsNullOrEmpty(settings.Model) ? "claude-sonnet-5" : settings.Model;
            UpdateKeyState();
            LoadExample();
            ShowPage(0);
        }

        // ---------------- marca / navegacion ----------------
        public static UIElement LogoMark(double size)
        {
            var g = new Grid { Width = size, Height = size };
            g.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(size * 0.27),
                Background = new LinearGradientBrush(Color.FromRgb(0x4D, 0xA3, 0xFF), Color.FromRgb(0x00, 0x51, 0xC9), 60)
            });
            g.Children.Add(new TextBlock { Text = "", FontFamily = Theme.Icons, FontSize = size * 0.52, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            return g;
        }

        RadioButton Nav(string glyph, string text, int index)
        {
            var rb = new RadioButton { GroupName = "nav" };
            rb.SetResourceReference(StyleProperty, "Nav");
            var p = new StackPanel { Orientation = Orientation.Horizontal };
            p.Children.Add(new TextBlock { Text = glyph, FontFamily = Theme.Icons, FontSize = 15, Width = 24, VerticalAlignment = VerticalAlignment.Center });
            p.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.Medium });
            rb.Content = p;
            rb.Checked += (s, e) => { if (pages != null) host.Content = pages[index]; };
            return rb;
        }

        void ShowPage(int i) { navButtons[i].IsChecked = true; host.Content = pages[i]; }

        UIElement Page(string title, string subtitle, UIElement content)
        {
            var d = new DockPanel { Margin = new Thickness(32, 26, 32, 12) };
            var h = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            h.Children.Add(new TextBlock { Text = title, FontSize = 30, FontFamily = Theme.FontDisplay, FontWeight = FontWeights.SemiBold });
            h.Children.Add(new TextBlock { Text = subtitle, Foreground = Theme.B(Theme.Muted), FontSize = 13.5, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
            DockPanel.SetDock(h, Dock.Top); d.Children.Add(h); d.Children.Add(content);
            return d;
        }

        // ---------------- helpers UI ----------------
        static TextBlock Lbl(string t) { return new TextBlock { Text = t, Margin = new Thickness(0, 10, 0, 4), FontWeight = FontWeights.SemiBold, FontSize = 13 }; }
        static TextBlock Note(string t) { return new TextBlock { Text = t, Foreground = Theme.B(Theme.Muted), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6), FontSize = 12.5 }; }
        static Button Btn(string t, RoutedEventHandler h, bool secondary = false)
        {
            var b = new Button { Content = t }; if (secondary) b.SetResourceReference(StyleProperty, "Secondary"); b.Click += h; return b;
        }
        static StackPanel Row(params UIElement[] c) { var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) }; foreach (var x in c) p.Children.Add(x); return p; }
        static TextBox Box(bool multi = false, bool mono = false)
        {
            var t = new TextBox { TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap };
            if (multi) { t.AcceptsReturn = true; t.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; }
            if (mono) { t.FontFamily = Mono; t.FontSize = 12.5; }
            return t;
        }
        void SetStatus(string s) { Dispatcher.Invoke(() => status.Text = s); }
        void Log(string s) { Dispatcher.Invoke(() => { txtLog.AppendText(s + Environment.NewLine); txtLog.ScrollToEnd(); }); }
        string ServerName { get { return cmbServer.Text.Trim(); } }
        string DsnName { get { return cmbDsn.Text.Trim(); } }
        void Say(string m) { Dispatcher.Invoke(() => MessageBox.Show(this, m, "EtlStudio", MessageBoxButton.OK, MessageBoxImage.Information)); }

        async void Work(string title, Action job, bool toLog = false)
        {
            SetStatus(title + "...");
            if (toLog) Log("▶ " + title);
            try { await Task.Run(job); SetStatus(title + ": OK"); if (toLog) Log("✔ " + title + ": OK"); }
            catch (Exception ex)
            {
                SetStatus(title + ": ERROR");
                if (toLog) Log("✖ " + title + ": " + ex.Message); else MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ---------------- entorno y ajustes ----------------
        void LoadEnvironment()
        {
            var inst = Environments.LocalSqlInstances();
            var running = new List<string>();
            foreach (var i in inst)
            {
                string svc = i == "localhost" ? "MSSQLSERVER" : "MSSQL$" + i.Substring(i.IndexOf('\\') + 1);
                try { if (new ServiceController(svc).Status == ServiceControllerStatus.Running) running.Add(i); } catch { }
            }
            cmbServer.ItemsSource = inst;
            cmbServer.Text = !string.IsNullOrEmpty(settings.SqlServer) && settings.SqlServer != "localhost" ? settings.SqlServer : (running.FirstOrDefault() ?? inst.FirstOrDefault() ?? "localhost");
            RefreshDsns();
            var dsns = SourceReader.ListDsns();
            cmbDsn.Text = !string.IsNullOrEmpty(settings.Dsn) ? settings.Dsn : (dsns.FirstOrDefault(d => d.IndexOf("databricks", StringComparison.OrdinalIgnoreCase) >= 0) ?? dsns.FirstOrDefault() ?? "");
        }

        void RefreshDsns() { cmbDsn.ItemsSource = SourceReader.ListDsns(); }

        UIElement BuildSettings()
        {
            var p = new StackPanel();
            var c1 = new StackPanel();
            c1.Children.Add(new TextBlock { Text = "Claude (API de Anthropic, de pago)", FontWeight = FontWeights.SemiBold, FontSize = 15 });
            c1.Children.Add(Note("Tu API key se guarda cifrada con DPAPI (solo tu usuario de Windows) en %APPDATA%\\EtlStudio, o se lee de ANTHROPIC_API_KEY. Nunca se sube a ningun repositorio."));
            pwdKey = new PasswordBox { Padding = new Thickness(10, 8, 10, 8) }; c1.Children.Add(pwdKey);
            cmbModel = new ComboBox { IsEditable = true, Margin = new Thickness(0, 8, 0, 0) }; c1.Children.Add(cmbModel);
            keyState = new TextBlock { Margin = new Thickness(0, 8, 0, 0) }; c1.Children.Add(keyState);
            c1.Children.Add(Row(Btn("Guardar", (s, e) => SaveSettings())));
            p.Children.Add(Theme.Card(c1));

            var c2 = new StackPanel();
            c2.Children.Add(new TextBlock { Text = "Conexiones", FontWeight = FontWeights.SemiBold, FontSize = 15 });
            c2.Children.Add(Note("El SQL Server y el DSN se eligen en Inicio. Aqui puedes probarlos. El token de Databricks vive en el DSN, no en esta app."));
            c2.Children.Add(Row(
                Btn("Probar SQL Server", (s, e) => { var sv = ServerName; Work("Probar SQL Server", () => Say(SqlRunner.Test(sv))); }, true),
                Btn("Probar ODBC", (s, e) => { var d = DsnName; Work("Probar ODBC", () => Say(SourceReader.Test(d))); }, true),
                Btn("Abrir administrador ODBC", (s, e) => Process.Start(System.IO.Path.Combine(Environment.SystemDirectory, "odbcad32.exe")), true)));
            p.Children.Add(Theme.Card(c2));
            return new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        void SaveSettings()
        {
            settings.SqlServer = ServerName; settings.Dsn = DsnName; settings.Model = cmbModel.Text.Trim();
            if (pwdKey.Password.Length > 0) { settings.SetApiKey(pwdKey.Password); pwdKey.Clear(); }
            settings.Save(); UpdateKeyState(); SetStatus("Ajustes guardados.");
        }

        void UpdateKeyState()
        {
            bool ok = !string.IsNullOrEmpty(settings.GetApiKey());
            keyState.Text = ok ? "✔  API key configurada" : "API key NO configurada";
            keyState.Foreground = Theme.B(ok ? Theme.Green : Theme.Red);
        }

        ClaudeClient Claude()
        {
            var key = settings.GetApiKey();
            if (string.IsNullOrEmpty(key)) throw new InvalidOperationException("Configura tu API key de Anthropic en Ajustes.");
            return new ClaudeClient(key, string.IsNullOrEmpty(settings.Model) ? "claude-sonnet-5" : settings.Model);
        }

        // ======================================================================
        //  INICIO: modo automatico
        // ======================================================================
        UIElement BuildHome()
        {
            var p = new StackPanel { Margin = new Thickness(36, 30, 36, 20), MaxWidth = 880, HorizontalAlignment = HorizontalAlignment.Left };
            p.Children.Add(new TextBlock { Text = "¿Qué quieres resolver?", FontSize = 36, FontFamily = Theme.FontDisplay, FontWeight = FontWeights.SemiBold });
            p.Children.Add(new TextBlock { Text = "Suelta tu enunciado o descríbelo. Claude arma el plan; tú lo apruebas y EtlStudio crea el DW, el paquete SSIS, carga los datos y te entrega un reporte.",
                Foreground = Theme.B(Theme.Muted), FontSize = 14.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 20) });

            // entorno
            var env = new Grid();
            env.ColumnDefinitions.Add(new ColumnDefinition()); env.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) }); env.ColumnDefinitions.Add(new ColumnDefinition());
            var a = new StackPanel(); a.Children.Add(Lbl("SQL Server (destino)")); a.Children.Add(cmbServer);
            var b = new StackPanel(); b.Children.Add(Lbl("Origen ODBC (DSN de Databricks)")); b.Children.Add(cmbDsn);
            Grid.SetColumn(b, 2); env.Children.Add(a); env.Children.Add(b);
            p.Children.Add(Theme.Card(env, 18));

            // zona de arrastre
            var dropGrid = new Grid();
            dropRect = new Rectangle { Stroke = Theme.B(Theme.Line), StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 4 }, RadiusX = 14, RadiusY = 14, Fill = Brushes.White };
            dropGrid.Children.Add(dropRect);
            var di = new StackPanel { Margin = new Thickness(20, 24, 20, 20), HorizontalAlignment = HorizontalAlignment.Center };
            di.Children.Add(new TextBlock { Text = "", FontFamily = Theme.Icons, FontSize = 32, Foreground = Theme.B(Theme.Blue), HorizontalAlignment = HorizontalAlignment.Center });
            di.Children.Add(new TextBlock { Text = "Suelta aquí tu enunciado", FontSize = 17, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) });
            di.Children.Add(new TextBlock { Text = "PPTX · DOCX · PDF · XLSX · imágenes · TXT · SQL", Foreground = Theme.B(Theme.Muted), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 10) });
            var pick = Btn("Elegir archivos…", (s, e) => PickFiles(), true); pick.HorizontalAlignment = HorizontalAlignment.Center; pick.Margin = new Thickness(0);
            di.Children.Add(pick);
            chips = new WrapPanel { Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            di.Children.Add(chips);
            dropGrid.Children.Add(di);
            dropGrid.Margin = new Thickness(0, 0, 0, 14);
            dropGrid.AllowDrop = true;
            dropGrid.DragEnter += (s, e) => { dropRect.Stroke = Theme.B(Theme.Blue); dropRect.Fill = Theme.B(Color.FromRgb(0xF0, 0xF7, 0xFF)); };
            dropGrid.DragLeave += (s, e) => { dropRect.Stroke = Theme.B(Theme.Line); dropRect.Fill = Brushes.White; };
            dropGrid.Drop += (s, e) =>
            {
                dropRect.Stroke = Theme.B(Theme.Line); dropRect.Fill = Brushes.White;
                var files = e.Data.GetData(DataFormats.FileDrop) as string[]; if (files != null) AddFiles(files);
            };
            p.Children.Add(dropGrid);

            // prompt
            var pg = new Grid();
            txtPrompt = Box(true); txtPrompt.Height = 118; txtPrompt.FontSize = 14.5;
            promptHint = new TextBlock { Text = "Describe lo que necesitas…  Ej.: “Resuelve la práctica adjunta: crea el DW, el SSIS con Databricks y las metas del Excel, y verifica todo”.",
                Foreground = Theme.B(Color.FromRgb(0xA1, 0xA1, 0xA6)), IsHitTestVisible = false, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(13, 11, 13, 0), FontSize = 14.5 };
            txtPrompt.TextChanged += (s, e) => promptHint.Visibility = txtPrompt.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            pg.Children.Add(txtPrompt); pg.Children.Add(promptHint);
            p.Children.Add(pg);

            // opciones y accion
            chkReview = new CheckBox { Content = "Revisar el plan antes de ejecutar", IsChecked = true, Margin = new Thickness(2, 12, 16, 4) };
            chkRecreateHome = new CheckBox { Content = "Recrear la base si ya existe (borra sus datos)", Margin = new Thickness(0, 12, 0, 4) };
            p.Children.Add(Row(chkReview, chkRecreateHome));
            var go = Btn("Resolver automáticamente  ✦", (s, e) => Solve()); go.SetResourceReference(StyleProperty, "Big"); go.Margin = new Thickness(0, 0, 10, 0);
            var clear = Btn("Limpiar", (s, e) => ClearHome(), true); clear.SetResourceReference(StyleProperty, "Secondary");
            p.Children.Add(Row(go, clear));
            p.Children.Add(Note("Privacidad: el texto de tus archivos se envía a la API de Anthropic con tokens y contraseñas ocultos automáticamente. Los PDF y las imágenes se envían completos (te preguntaré antes)."));

            // tarjetas de estado
            var progBody = new StackPanel();
            progressTitle = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold };
            bar = new ProgressBar { Height = 4, IsIndeterminate = true, Margin = new Thickness(0, 10, 0, 6), Foreground = Theme.B(Theme.Blue), BorderThickness = new Thickness(0), Background = Theme.B(Color.FromRgb(0xE8, 0xE8, 0xED)) };
            stepsPanel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            progressDetail = new TextBlock { Foreground = Theme.B(Theme.Muted), FontSize = 12, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
            progBody.Children.Add(progressTitle); progBody.Children.Add(bar); progBody.Children.Add(stepsPanel); progBody.Children.Add(progressDetail);
            cardProgress = Theme.Card(progBody); cardProgress.Visibility = Visibility.Collapsed;

            var planBodyPanel = new StackPanel();
            planTitle = new TextBlock { FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            planBody = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8), LineHeight = 21 };
            planBodyPanel.Children.Add(new TextBlock { Text = "PLAN PROPUESTO", FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.B(Theme.Blue) });
            planBodyPanel.Children.Add(planTitle); planBodyPanel.Children.Add(planBody);
            planBodyPanel.Children.Add(Row(Btn("Aprobar y ejecutar", (s, e) => approval?.TrySetResult(true)), Btn("Editar en Diseño", (s, e) => { approval?.TrySetResult(false); ShowPage(1); }, true), Btn("Cancelar", (s, e) => approval?.TrySetResult(false), true)));
            cardPlan = Theme.Card(planBodyPanel); cardPlan.Visibility = Visibility.Collapsed;

            var resBody = new StackPanel();
            resultTitle = new TextBlock { FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Theme.B(Theme.Green) };
            resultBody = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4), LineHeight = 21 };
            resBody.Children.Add(resultTitle); resBody.Children.Add(resultBody);
            resBody.Children.Add(Row(
                Btn("Abrir reporte (.docx)", (s, e) => OpenPath(lastResult?.ReportPath)),
                Btn("Ver verificación", (s, e) => ShowPage(3), true),
                Btn("Carpeta del paquete", (s, e) => { if (lastResult != null) Process.Start("explorer.exe", "/select,\"" + lastResult.PackagePath + "\""); }, true)));
            cardResult = Theme.Card(resBody); cardResult.Visibility = Visibility.Collapsed;

            var errBody = new StackPanel();
            errBody.Children.Add(new TextBlock { Text = "Algo necesita tu atención", FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Theme.B(Theme.Red) });
            errorBody = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4), LineHeight = 20 };
            errBody.Children.Add(errorBody);
            btnOpenOdbc = Btn("Abrir administrador ODBC", (s, e) => Process.Start(System.IO.Path.Combine(Environment.SystemDirectory, "odbcad32.exe")));
            errBody.Children.Add(Row(btnOpenOdbc, Btn("Pedir ayuda a Claude", (s, e) => AskAboutError(), true)));
            cardError = Theme.Card(errBody); cardError.Visibility = Visibility.Collapsed;

            p.Children.Add(cardProgress); p.Children.Add(cardPlan); p.Children.Add(cardResult); p.Children.Add(cardError);
            return new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        static void OpenPath(string p) { if (!string.IsNullOrEmpty(p) && File.Exists(p)) Process.Start(p); }

        void PickFiles()
        {
            var d = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = "Documentos y capturas|" + string.Join(";", DocReader.Supported.Select(x => "*" + x)) + "|Todos|*.*" };
            if (d.ShowDialog() == true) AddFiles(d.FileNames);
        }

        void AddFiles(IEnumerable<string> files)
        {
            foreach (var f in files)
            {
                try { var a = DocReader.Load(f); attachments.Add(a); }
                catch (Exception ex) { MessageBox.Show(this, "No pude leer " + System.IO.Path.GetFileName(f) + ":\n" + ex.Message, "Adjuntar", MessageBoxButton.OK, MessageBoxImage.Warning); }
            }
            RenderChips();
        }

        void RenderChips()
        {
            chips.Children.Clear();
            foreach (var a in attachments.ToList())
            {
                var att = a;
                var x = new Button { Content = "✕", Padding = new Thickness(6, 0, 0, 0), Margin = new Thickness(6, 0, 0, 0), Background = Brushes.Transparent, Foreground = Theme.B(Theme.Muted), BorderThickness = new Thickness(0), FontSize = 11, Cursor = Cursors.Hand };
                x.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'><ContentPresenter/></ControlTemplate>");
                x.Click += (s, e) => { attachments.Remove(att); RenderChips(); };
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new TextBlock { Text = att.Summary, FontSize = 12 }); sp.Children.Add(x);
                chips.Children.Add(new Border { Background = Theme.B(Color.FromRgb(0xEE, 0xF4, 0xFF)), CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 5, 8, 5), Margin = new Thickness(4), Child = sp });
            }
        }

        void ClearHome()
        {
            if (busy) return;
            attachments.Clear(); RenderChips(); txtPrompt.Clear();
            foreach (var c in new[] { cardProgress, cardPlan, cardResult, cardError }) c.Visibility = Visibility.Collapsed;
        }

        void HideCards() { foreach (var c in new[] { cardProgress, cardPlan, cardResult, cardError }) c.Visibility = Visibility.Collapsed; }

        void BuildSteps()
        {
            stepsPanel.Children.Clear(); stepGlyph.Clear();
            foreach (var st in Pipeline.Steps)
            {
                var g = new TextBlock { Text = "○", Width = 24, FontSize = 15, Foreground = Theme.B(Color.FromRgb(0xA1, 0xA1, 0xA6)) };
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
                row.Children.Add(g); row.Children.Add(new TextBlock { Text = st[1], FontSize = 13.5 });
                stepsPanel.Children.Add(row); stepGlyph[st[0]] = g;
            }
        }

        void SetStep(string id, string st)
        {
            Dispatcher.Invoke(() =>
            {
                TextBlock g; if (!stepGlyph.TryGetValue(id, out g)) return;
                if (st == "run") { g.Text = "◐"; g.Foreground = Theme.B(Theme.Blue); }
                else if (st == "ok") { g.Text = "✔"; g.Foreground = Theme.B(Theme.Green); }
                else { g.Text = "✖"; g.Foreground = Theme.B(Theme.Red); }
            });
        }

        string lastError = "";

        async void Solve()
        {
            if (busy) return;
            string prompt = txtPrompt.Text.Trim();
            if (prompt.Length == 0 && attachments.Count == 0) { MessageBox.Show(this, "Escribe lo que necesitas o suelta un archivo.", "EtlStudio"); return; }
            if (string.IsNullOrEmpty(settings.GetApiKey())) { MessageBox.Show(this, "Primero configura tu API key de Anthropic en Ajustes.", "EtlStudio"); ShowPage(5); return; }
            if (attachments.Any(a => a.Kind != "text") &&
                MessageBox.Show(this, "Vas a enviar PDF o imágenes completos a la API de Anthropic. Pueden contener credenciales que no puedo ocultar.\n\n¿Continuar?", "Privacidad", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            busy = true; HideCards(); lastError = "";
            string server = ServerName, dsnChoice = DsnName; bool review = chkReview.IsChecked == true, recreate = chkRecreateHome.IsChecked == true;
            var atts = attachments.ToList();
            progressTitle.Text = "Leyendo tu enunciado y planificando con Claude…"; bar.IsIndeterminate = true; stepsPanel.Children.Clear(); progressDetail.Text = "";
            cardProgress.Visibility = Visibility.Visible;
            try
            {
                var plan = await Task.Run(() => PlanWithClaude(prompt, atts, server, dsnChoice));
                txtSpec.Text = plan.Spec.ToJson(); txtQuery.Text = plan.Spec.SourceQuery;

                if (review)
                {
                    cardProgress.Visibility = Visibility.Collapsed;
                    ShowPlan(plan);
                    approval = new TaskCompletionSource<bool>();
                    if (!await approval.Task) { cardPlan.Visibility = Visibility.Collapsed; SetStatus("Plan cancelado."); return; }
                    cardPlan.Visibility = Visibility.Collapsed; cardProgress.Visibility = Visibility.Visible;
                }
                progressTitle.Text = "Construyendo tu Data Warehouse…"; bar.IsIndeterminate = false; bar.Value = 0; BuildSteps();
                var outDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EtlStudio", plan.Spec.DwName);
                int done = 0;
                var res = await Task.Run(() => Pipeline.Run(plan, recreate, outDir,
                    (id, st) => { SetStep(id, st); if (st == "ok") { done++; Dispatcher.Invoke(() => bar.Value = 100.0 * done / Pipeline.Steps.Length); } },
                    m => { Log(m); Dispatcher.Invoke(() => progressDetail.Text = m); }));
                lastResult = res; lastVerification = res.VerificationText; PopulateVerify(res.Checks);
                cardProgress.Visibility = Visibility.Collapsed;
                resultTitle.Text = "✔  Listo";
                var counts = res.Checks.Count > 0 ? res.Checks[0].Value : null;
                var sb = new StringBuilder(plan.Resumen + "\n\n");
                if (counts != null) foreach (DataRow r in counts.Rows) sb.AppendLine("•  " + r[0] + ":  " + string.Format("{0:N0}", r[1]));
                sb.AppendLine("\nReporte: " + res.ReportPath);
                resultBody.Text = sb.ToString(); cardResult.Visibility = Visibility.Visible;
                SetStatus("Proceso completo.");
            }
            catch (NeedsTokenException ex) { ShowError(ex.Message, true); }
            catch (Exception ex) { ShowError(ex.Message, false); }
            finally { busy = false; }
        }

        void ShowError(string msg, bool needsToken)
        {
            lastError = msg; cardProgress.Visibility = Visibility.Collapsed; cardPlan.Visibility = Visibility.Collapsed;
            errorBody.Text = msg; btnOpenOdbc.Visibility = needsToken ? Visibility.Visible : Visibility.Collapsed;
            cardError.Visibility = Visibility.Visible; SetStatus("Requiere atención.");
        }

        void AskAboutError()
        {
            ShowPage(4);
            AskClaude("Me salio este error en EtlStudio. ¿Que significa y como lo resuelvo?\n" + lastError);
        }

        void ShowPlan(Plan plan)
        {
            planTitle.Text = string.IsNullOrWhiteSpace(plan.Titulo) ? plan.Spec.DwName : plan.Titulo;
            var s = plan.Spec; var sb = new StringBuilder();
            sb.AppendLine(plan.Resumen); sb.AppendLine();
            sb.AppendLine("•  Base de datos:  " + s.DwName + "  en  " + s.SqlServer);
            sb.AppendLine("•  Origen:  DSN " + s.SourceDsn + (Environments.DsnExists(s.SourceDsn) ? "" : "  (se creará; luego pegas tu token)"));
            sb.AppendLine("•  Dimensiones:  " + string.Join(", ", s.Dims.Select(d => d.Name)) + ", " + s.Time.Name);
            sb.AppendLine("•  Hechos:  " + s.Fact.Name + "  (" + s.Fact.Measures.Count + " medidas" + (s.Fact.Computed.Count > 0 ? ", " + s.Fact.Computed.Count + " calculadas" : "") + ")");
            if (s.Goals != null) sb.AppendLine("•  Metas desde Excel:  " + s.Goals.Table + "  ←  " + System.IO.Path.GetFileName(s.Goals.ExcelPath));
            if (plan.Pasos.Count > 0) { sb.AppendLine("\nPasos:"); foreach (var p in plan.Pasos) sb.AppendLine("  –  " + p); }
            if (plan.Advertencias.Count > 0) { sb.AppendLine("\nA tener en cuenta:"); foreach (var w in plan.Advertencias) sb.AppendLine("  ⚠  " + w); }
            planBody.Text = sb.ToString(); cardPlan.Visibility = Visibility.Visible;
        }

        /// <summary>Pide a Claude el plan; si el diseno no valida, se lo devuelve UNA vez con los errores para que lo corrija.</summary>
        Plan PlanWithClaude(string prompt, List<Attachment> atts, string server, string dsnChoice)
        {
            var claude = Claude();
            var sb = new StringBuilder("TAREA DEL USUARIO:\n" + (prompt.Length > 0 ? prompt : "(sin texto; usa los archivos adjuntos)") + "\n\nENTORNO LOCAL:\n");
            sb.AppendLine("- SQL Server elegido: " + server + "  (detectados: " + string.Join(", ", Environments.LocalSqlInstances()) + ")");
            sb.AppendLine("- DSN ODBC existentes: " + string.Join(", ", SourceReader.ListDsns()) + "  (elegido: " + dsnChoice + ")");
            foreach (var x in atts.Where(a => a.Path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))) sb.AppendLine("- Excel adjunto en: " + x.Path);

            var turn = new ClaudeClient.Turn { Role = "user", Text = sb.ToString(), Attachments = atts };
            var turns = new List<ClaudeClient.Turn> { turn };
            Plan plan = null; List<string> errs = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var reply = claude.Send(ClaudeClient.PlannerSystemPrompt, turns);
                try
                {
                    plan = Plan.FromJson(ClaudeClient.ExtractJson(reply));
                    Normalize(plan, atts, server, dsnChoice);
                    errs = plan.Spec.Validate();
                }
                catch (Exception ex) { errs = new List<string> { "JSON invalido: " + ex.Message }; }
                if (errs.Count == 0) return plan;
                turns.Add(new ClaudeClient.Turn { Role = "assistant", Text = reply });
                turns.Add(new ClaudeClient.Turn { Role = "user", Text = "El diseno no es valido. Corrigelo y devuelve el JSON completo otra vez:\n - " + string.Join("\n - ", errs) });
            }
            throw new Exception("Claude no logro un diseño válido:\n - " + string.Join("\n - ", errs) + "\n\nRevisa el enunciado o edítalo a mano en Diseño.");
        }

        static void Normalize(Plan plan, List<Attachment> atts, string server, string dsnChoice)
        {
            var s = plan.Spec; s.SqlServer = server;   // conexion siempre la decide el usuario, no la IA
            string dsn = plan.Conexion != null && !string.IsNullOrWhiteSpace(plan.Conexion.DsnSugerido) ? plan.Conexion.DsnSugerido.Trim() : dsnChoice;
            bool canCreate = plan.Conexion != null && !string.IsNullOrWhiteSpace(plan.Conexion.Host) && !string.IsNullOrWhiteSpace(plan.Conexion.HttpPath);
            if (!Environments.DsnExists(dsn) && !canCreate && Environments.DsnExists(dsnChoice)) dsn = dsnChoice;
            s.SourceDsn = dsn;
            if (s.Goals != null)
            {
                var x = atts.FirstOrDefault(a => a.Path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase));
                if (x != null) s.Goals.ExcelPath = x.Path;
                if (!File.Exists(s.Goals.ExcelPath)) { s.Goals = null; plan.Advertencias.Add("Se omitieron las metas de Excel: no hay un .xlsx adjunto."); }
            }
        }

        // ======================================================================
        //  Fuente / diseno (avanzado)
        // ======================================================================
        UIElement BuildDesign()
        {
            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(120) });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var l = Lbl("SELECT de origen (lo que se lleva al DW)"); Grid.SetRow(l, 0); g.Children.Add(l);
            txtQuery = Box(true, true); Grid.SetRow(txtQuery, 1); g.Children.Add(txtQuery);
            var r = Row(
                Btn("Vista previa", (s, e) => Preview(), true),
                Btn("Proponer DW con Claude", (s, e) => ProposeWithClaude()),
                Btn("Ejemplo: Autos", (s, e) => LoadExample(), true),
                Btn("Validar", (s, e) => ValidateSpec(), true),
                Btn("Abrir…", (s, e) => OpenSpec(), true), Btn("Guardar…", (s, e) => SaveSpec(), true));
            Grid.SetRow(r, 2); g.Children.Add(r);
            var split = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            split.ColumnDefinitions.Add(new ColumnDefinition()); split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) }); split.ColumnDefinitions.Add(new ColumnDefinition());
            txtSpec = Box(true, true); split.Children.Add(txtSpec);
            gridPreview = new DataGrid(); var gb = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(10), BorderBrush = Theme.B(Theme.Line), BorderThickness = new Thickness(1), Child = gridPreview, ClipToBounds = true };
            Grid.SetColumn(gb, 2); split.Children.Add(gb);
            txtHint = Box(); txtHint.Visibility = Visibility.Collapsed;
            Grid.SetRow(split, 3); g.Children.Add(split);
            return g;
        }

        void Preview()
        {
            string dsn = DsnName, q = txtQuery.Text.Trim();
            Work("Vista previa", () =>
            {
                var dt = SourceReader.Preview(dsn, q, 20); lastPreview = dt;
                Dispatcher.Invoke(() => gridPreview.ItemsSource = dt.DefaultView);
            });
        }

        void LoadExample()
        {
            var s = DwSpec.ExampleAutos(); if (!string.IsNullOrEmpty(ServerName)) s.SqlServer = ServerName; if (!string.IsNullOrEmpty(DsnName)) s.SourceDsn = DsnName;
            txtSpec.Text = s.ToJson(); txtQuery.Text = s.SourceQuery;
        }

        DwSpec ReadSpec()
        {
            DwSpec s;
            try { s = DwSpec.FromJson(txtSpec.Text); } catch (Exception ex) { throw new InvalidOperationException("El JSON del diseño no es válido: " + ex.Message); }
            var errs = s.Validate();
            if (errs.Count > 0) throw new InvalidOperationException("El diseño tiene problemas:\n - " + string.Join("\n - ", errs));
            return s;
        }

        void ValidateSpec()
        {
            try { var s = ReadSpec(); SetStatus("Diseño válido: " + s.Dims.Count + " dimensiones, " + s.Fact.Measures.Count + " medidas."); MessageBox.Show(this, "El diseño es válido.", "Validar"); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Validar", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        void OpenSpec() { var d = new Microsoft.Win32.OpenFileDialog { Filter = "Diseño JSON|*.json" }; if (d.ShowDialog() == true) txtSpec.Text = File.ReadAllText(d.FileName); }
        void SaveSpec() { var d = new Microsoft.Win32.SaveFileDialog { Filter = "Diseño JSON|*.json", FileName = "dw_spec.json" }; if (d.ShowDialog() == true) File.WriteAllText(d.FileName, txtSpec.Text); }

        void ProposeWithClaude()
        {
            string dsn = DsnName, server = ServerName, q = txtQuery.Text.Trim();
            Work("Proponer DW con Claude", () =>
            {
                var claude = Claude();
                var dt = lastPreview ?? SourceReader.Preview(dsn, q, 20);
                var msg = "SELECT de origen:\n" + q + "\n\nEsquema:\n" + SourceReader.DescribeSchema(dt) + "\nFilas de muestra:\n" + SourceReader.SampleRows(dt, 8);
                var reply = claude.Send(ClaudeClient.DesignSystemPrompt, new[] { new ClaudeClient.Turn { Role = "user", Text = msg } });
                var spec = DwSpec.FromJson(ClaudeClient.ExtractJson(reply));
                spec.SqlServer = server; spec.SourceDsn = dsn; spec.SourceQuery = q;
                var errs = spec.Validate();
                Dispatcher.Invoke(() =>
                {
                    txtSpec.Text = spec.ToJson();
                    if (errs.Count > 0) MessageBox.Show(this, "Claude propuso un diseño con problemas; corrígelo o vuelve a pedirlo:\n - " + string.Join("\n - ", errs), "Diseño", MessageBoxButton.OK, MessageBoxImage.Warning);
                });
            });
        }

        // ======================================================================
        //  Ejecutar (avanzado)
        // ======================================================================
        UIElement BuildRun()
        {
            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var top = new StackPanel();
            top.Children.Add(Lbl("Archivo del paquete SSIS (.dtsx)"));
            txtOut = Box(); txtOut.Text = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EtlStudio", "ETL.dtsx"); top.Children.Add(txtOut);
            chkRecreate = new CheckBox { Content = "Recrear la base de datos (BORRA la base existente y sus datos)", Foreground = Theme.B(Theme.Red), Margin = new Thickness(0, 10, 0, 0) };
            top.Children.Add(chkRecreate);
            top.Children.Add(Row(
                Btn("1. Crear DW", (s, e) => CreateDw()), Btn("2. Generar paquete", (s, e) => GeneratePackage()),
                Btn("3. Validar", (s, e) => RunPackage(true)), Btn("4. Ejecutar carga", (s, e) => RunPackage(false)),
                Btn("Todo en uno", (s, e) => AllInOne(), true), Btn("Ver DDL", (s, e) => ShowDdl(), true), Btn("Abrir carpeta", (s, e) => OpenFolder(), true)));
            Grid.SetRow(top, 0); g.Children.Add(top);
            txtLog = Box(true, true); txtLog.IsReadOnly = true; txtLog.Margin = new Thickness(0, 12, 0, 0);
            txtLog.Background = Theme.B(Color.FromRgb(0x1C, 0x1C, 0x1E)); txtLog.Foreground = Theme.B(Color.FromRgb(0xE5, 0xE5, 0xEA));
            Grid.SetRow(txtLog, 1); g.Children.Add(txtLog);
            return g;
        }

        void OpenFolder() { var d = System.IO.Path.GetDirectoryName(txtOut.Text.Trim()); Directory.CreateDirectory(d); Process.Start(d); }

        void ShowDdl()
        {
            try { var s = ReadSpec(); new Window { Title = "DDL generado", Width = 900, Height = 650, Owner = this, Content = new TextBox { Text = DdlGenerator.Tables(s), IsReadOnly = true, FontFamily = Mono, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } }.Show(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "DDL", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        bool TrySpec(out DwSpec s)
        {
            try { s = ReadSpec(); return true; }
            catch (Exception ex) { s = null; MessageBox.Show(this, ex.Message, "Diseño", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
        }

        bool ConfirmRecreate(DwSpec s)
        {
            return chkRecreate.IsChecked != true || MessageBox.Show(this, "Se BORRARÁ la base '" + s.DwName + "' con todos sus datos. ¿Continuar?", "Confirmar", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }

        void CreateDw() { DwSpec s; if (!TrySpec(out s) || !ConfirmRecreate(s)) return; bool rc = chkRecreate.IsChecked == true; Work("Crear DW", () => CreateDwCore(s, rc), true); }

        void CreateDwCore(DwSpec s, bool recreate)
        {
            if (recreate) SqlRunner.Exec(s.SqlServer, "master", DdlGenerator.DropDatabase(s));
            SqlRunner.Exec(s.SqlServer, "master", DdlGenerator.CreateDatabase(s));
            SqlRunner.Exec(s.SqlServer, s.DwName, DdlGenerator.Tables(s));
            Log("   Base '" + s.DwName + "' y tablas listas en " + s.SqlServer + ".");
        }

        void GeneratePackage() { DwSpec s; if (!TrySpec(out s)) return; string path = txtOut.Text.Trim(); Work("Generar paquete", () => GenerateCore(s, path), true); }

        void GenerateCore(DwSpec s, string path)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            new SsisBuilder(s).Build(path);
            Log("   Paquete guardado en " + path + ". Puedes agregarlo a Visual Studio con 'Add Existing Package'.");
        }

        void RunPackage(bool validateOnly)
        {
            string path = txtOut.Text.Trim();
            if (!File.Exists(path)) { MessageBox.Show(this, "Primero genera el paquete.", "Paquete"); return; }
            Work(validateOnly ? "Validar paquete" : "Ejecutar carga", () => RunCore(path, validateOnly), true);
        }

        void RunCore(string path, bool validateOnly)
        {
            int code = DtExec.Run(path, validateOnly, l => { if (l.Trim().Length > 0 && !l.StartsWith("Copyright")) Log("   " + l); });
            if (code != 0) throw new Exception("dtexec devolvio el codigo " + code + ". Pidele a Claude que lo explique (Asistente).");
        }

        void AllInOne()
        {
            DwSpec s; if (!TrySpec(out s) || !ConfirmRecreate(s)) return; bool rc = chkRecreate.IsChecked == true; string path = txtOut.Text.Trim();
            Work("Proceso completo", () =>
            {
                Log("▶ 1/4 Crear DW"); CreateDwCore(s, rc);
                Log("▶ 2/4 Generar paquete"); GenerateCore(s, path);
                Log("▶ 3/4 Validar"); RunCore(path, true);
                Log("▶ 4/4 Ejecutar carga (puede tardar unos minutos)"); RunCore(path, false);
                Log("✔ Listo. Ve a Verificar.");
            }, true);
        }

        // ======================================================================
        //  Verificar / asistente
        // ======================================================================
        UIElement BuildVerify()
        {
            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var r = Row(Btn("Ejecutar verificaciones", (s, e) => RunVerification()), Btn("Explicar con Claude", (s, e) => ExplainVerification(), true));
            Grid.SetRow(r, 0); g.Children.Add(r);
            verifyPanel = new StackPanel();
            var sv = new ScrollViewer { Content = verifyPanel, Margin = new Thickness(0, 8, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetRow(sv, 1); g.Children.Add(sv);
            return g;
        }

        void PopulateVerify(List<KeyValuePair<Check, DataTable>> checks)
        {
            verifyPanel.Children.Clear();
            foreach (var kv in checks)
            {
                var c = kv.Key; var body = new StackPanel();
                body.Children.Add(new TextBlock { Text = c.Title, FontWeight = FontWeights.SemiBold, FontSize = 14.5 });
                body.Children.Add(Note(c.Explain));
                var sql = new TextBox { Text = c.Sql, IsReadOnly = true, FontFamily = Mono, FontSize = 12, Background = Theme.B(Theme.Bg) }; body.Children.Add(sql);
                var grid = new DataGrid { ItemsSource = kv.Value.DefaultView, MaxHeight = 230, Margin = new Thickness(0, 8, 0, 0) }; body.Children.Add(grid);
                verifyPanel.Children.Add(Theme.Card(body));
            }
        }

        void RunVerification()
        {
            DwSpec s; if (!TrySpec(out s)) return;
            Work("Verificar", () =>
            {
                var list = new List<KeyValuePair<Check, DataTable>>(); var report = new StringBuilder();
                foreach (var c in Verification.For(s))
                {
                    var dt = SqlRunner.Query(s.SqlServer, s.DwName, c.Sql);
                    list.Add(new KeyValuePair<Check, DataTable>(c, dt));
                    report.AppendLine("## " + c.Title).AppendLine(SourceReader.SampleRows(dt, 12));
                }
                lastVerification = report.ToString();
                Dispatcher.Invoke(() => PopulateVerify(list));
            });
        }

        void ExplainVerification()
        {
            if (lastVerification.Length == 0) { MessageBox.Show(this, "Primero ejecuta las verificaciones.", "Verificar"); return; }
            ShowPage(4);
            AskClaude("Explica estos resultados de verificacion del DW y dime si algo se ve mal:\n" + lastVerification);
        }

        UIElement BuildChat()
        {
            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            txtChat = Box(true); txtChat.IsReadOnly = true; txtChat.FontSize = 13.5; Grid.SetRow(txtChat, 0); g.Children.Add(txtChat);
            var p = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            var send = Btn("Enviar", (s, e) => SendChat()); send.Margin = new Thickness(10, 0, 0, 0); DockPanel.SetDock(send, Dock.Right); p.Children.Add(send);
            txtAsk = Box(); txtAsk.KeyDown += (s, e) => { if (e.Key == Key.Enter) SendChat(); };
            p.Children.Add(txtAsk); Grid.SetRow(p, 1); g.Children.Add(p);
            return g;
        }

        void SendChat() { var q = txtAsk.Text.Trim(); if (q.Length > 0) { txtAsk.Clear(); AskClaude(q); } }

        void AskClaude(string question)
        {
            string spec = txtSpec.Text; string log = string.Join("\n", txtLog.Text.Split('\n').Reverse().Take(60).Reverse());
            chat.Add(new ClaudeClient.Turn { Role = "user", Text = question });
            txtChat.AppendText("Tú: " + question + Environment.NewLine + Environment.NewLine);
            var snapshot = chat.ToList();
            Work("Consultar a Claude", () =>
            {
                var sys = ClaudeClient.ChatSystemPrompt + "\n\nDiseño actual (JSON):\n" + spec + "\n\nÚltimas líneas del log:\n" + log;
                var reply = Claude().Send(sys, snapshot);
                chat.Add(new ClaudeClient.Turn { Role = "assistant", Text = reply });
                Dispatcher.Invoke(() => { txtChat.AppendText("Claude: " + reply + Environment.NewLine + Environment.NewLine); txtChat.ScrollToEnd(); });
            });
        }
    }
}

