using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using EtlStudio.UI;

namespace EtlStudio.Setup
{
    /// <summary>Instalador de EtlStudio (por usuario, sin permisos de administrador).
    /// Uso: EtlStudio-Setup.exe            -> asistente grafico
    ///      EtlStudio-Setup.exe /S          -> instalacion silenciosa
    ///      Uninstall.exe /uninstall [/S]   -> desinstalar</summary>
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            bool silent = args.Contains("/S", StringComparer.OrdinalIgnoreCase);
            if (args.Contains("/uninstall", StringComparer.OrdinalIgnoreCase))
            {
                if (silent) { try { Installer.Uninstall(false); return 0; } catch { return 1; } }
                return new Application().Run(new SetupWindow(true));
            }
            if (silent) { try { Installer.Install(true, false, s => { }); return 0; } catch { return 1; } }
            return new Application().Run(new SetupWindow(false));
        }
    }

    public static class Installer
    {
        public const string AppName = "EtlStudio";
        public const string Version = "1.1.0";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\EtlStudio";

        public static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName); } }
        static string StartMenu { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"); } }
        static string DesktopLnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"); } }

        static void Extract(string resource, string target)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            using (var f = File.Create(target)) s.CopyTo(f);
        }

        public static void Install(bool desktop, bool launch, Action<string> progress)
        {
            progress("Cerrando EtlStudio si esta abierto...");
            foreach (var p in Process.GetProcessesByName(AppName)) { try { p.Kill(); p.WaitForExit(3000); } catch { } }

            progress("Copiando archivos...");
            Directory.CreateDirectory(Dir);
            string exe = Path.Combine(Dir, AppName + ".exe");
            Extract("payload.exe", exe);
            Extract("payload.ico", Path.Combine(Dir, AppName + ".ico"));
            File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(Dir, "Uninstall.exe"), true);

            progress("Creando accesos directos...");
            MakeShortcut(StartMenu, exe);
            if (desktop) MakeShortcut(DesktopLnk, exe);

            progress("Registrando en Aplicaciones instaladas...");
            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", AppName);
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", "EtlStudio");
                k.SetValue("DisplayIcon", exe + ",0");
                k.SetValue("InstallLocation", Dir);
                k.SetValue("UninstallString", "\"" + Path.Combine(Dir, "Uninstall.exe") + "\" /uninstall");
                k.SetValue("QuietUninstallString", "\"" + Path.Combine(Dir, "Uninstall.exe") + "\" /uninstall /S");
                k.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
            if (launch) Process.Start(exe);
        }

        static void MakeShortcut(string lnk, string exe)
        {
            dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic l = sh.CreateShortcut(lnk);
            l.TargetPath = exe; l.WorkingDirectory = Path.GetDirectoryName(exe);
            l.IconLocation = exe + ",0"; l.Description = "EtlStudio: Data Warehouse + SSIS con Claude"; l.Save();
        }

        public static void Uninstall(bool removeSettings)
        {
            foreach (var p in Process.GetProcessesByName(AppName)) { try { p.Kill(); p.WaitForExit(3000); } catch { } }
            try { File.Delete(StartMenu); } catch { }
            try { File.Delete(DesktopLnk); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            if (removeSettings)
                try { Directory.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName), true); } catch { }
            // Uninstall.exe se esta ejecutando: se borra la carpeta cuando termine.
            var psi = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"" + Dir + "\"") { CreateNoWindow = true, UseShellExecute = false };
            Process.Start(psi);
        }
    }

    public class SetupWindow : Window
    {
        readonly bool uninstall;
        CheckBox chkDesktop, chkLaunch, chkSettings;
        Button btnMain, btnCancel;
        TextBlock info; ProgressBar bar;

        public SetupWindow(bool uninstall)
        {
            this.uninstall = uninstall;
            Title = uninstall ? "Desinstalar EtlStudio" : "Instalar EtlStudio";
            Width = 560; Height = 430; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Theme.Apply(this);
            try { Icon = LoadIcon(); } catch { }

            var p = new StackPanel { Margin = new Thickness(36, 30, 36, 24) };
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            try { head.Children.Add(new Image { Source = LoadIcon(), Width = 64, Height = 64 }); } catch { }
            var ht = new StackPanel { Margin = new Thickness(16, 4, 0, 0) };
            ht.Children.Add(new TextBlock { Text = uninstall ? "Desinstalar EtlStudio" : "Instalar EtlStudio", FontSize = 26, FontFamily = Theme.FontDisplay, FontWeight = FontWeights.SemiBold });
            ht.Children.Add(new TextBlock { Text = "Versión " + Installer.Version, Foreground = Theme.B(Theme.Muted) });
            head.Children.Add(ht); p.Children.Add(head);

            info = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 20, 0, 12), Foreground = Theme.B(Theme.Muted), FontSize = 13.5, LineHeight = 20,
                Text = uninstall ? "Se quitará EtlStudio de este equipo. Tus paquetes y reportes en Documentos no se tocan."
                                 : "Data Warehouse + paquetes SSIS con Claude, desde tu enunciado.\n\nSe instalará solo para tu usuario en:\n" + Installer.Dir + "\n\nNo necesita permisos de administrador." };
            p.Children.Add(info);
            if (uninstall) { chkSettings = new CheckBox { Content = "Eliminar también mis ajustes y mi API key guardada" }; p.Children.Add(chkSettings); }
            else
            {
                chkDesktop = new CheckBox { Content = "Crear un acceso directo en el escritorio", IsChecked = true }; p.Children.Add(chkDesktop);
                chkLaunch = new CheckBox { Content = "Abrir EtlStudio al terminar", IsChecked = true }; p.Children.Add(chkLaunch);
            }
            bar = new ProgressBar { Height = 4, IsIndeterminate = true, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 14, 0, 0), BorderThickness = new Thickness(0), Foreground = Theme.B(Theme.Blue) };
            p.Children.Add(bar);

            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
            btnCancel = new Button { Content = "Cancelar" }; btnCancel.SetResourceReference(StyleProperty, "Secondary"); btnCancel.Click += (s, e) => Close();
            btnMain = new Button { Content = uninstall ? "Desinstalar" : "Instalar", Margin = new Thickness(0) }; btnMain.Click += async (s, e) => await Go();
            row.Children.Add(btnCancel); row.Children.Add(btnMain); p.Children.Add(row);
            Content = p;
        }

        static ImageSource LoadIcon()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.ico"))
            {
                var dec = new IconBitmapDecoder(s, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                return dec.Frames.OrderBy(f => f.PixelWidth).Last();
            }
        }

        async Task Go()
        {
            btnMain.IsEnabled = false; btnCancel.IsEnabled = false; bar.Visibility = Visibility.Visible;
            try
            {
                if (uninstall)
                {
                    bool rs = chkSettings.IsChecked == true;
                    await Task.Run(() => Installer.Uninstall(rs));
                    Finish("EtlStudio se desinstaló.");
                }
                else
                {
                    bool desk = chkDesktop.IsChecked == true, launch = chkLaunch.IsChecked == true;
                    await Task.Run(() => Installer.Install(desk, launch, m => Dispatcher.Invoke(() => info.Text = m)));
                    Finish("EtlStudio se instaló correctamente.\n\nLo encuentras en el menú Inicio.\n\nPara usar Claude, pega tu API key en Ajustes.");
                }
            }
            catch (Exception ex) { bar.Visibility = Visibility.Collapsed; info.Text = "Ocurrió un error: " + ex.Message; btnCancel.IsEnabled = true; btnCancel.Content = "Cerrar"; }
        }

        void Finish(string msg)
        {
            bar.Visibility = Visibility.Collapsed; info.Text = msg; info.Foreground = Theme.B(Theme.Green);
            btnMain.Content = "Listo"; btnMain.IsEnabled = true; btnMain.Click -= null; btnMain.Click += (s, e) => Close(); btnCancel.Visibility = Visibility.Collapsed;
        }
    }
}
