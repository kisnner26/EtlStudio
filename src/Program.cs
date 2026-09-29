using System;
using System.IO;
using System.Linq;
using System.Windows;
using EtlStudio.Core;
using EtlStudio.UI;

namespace EtlStudio
{
    public static class Program
    {
        /// <summary>
        /// Sin argumentos: abre la interfaz grafica.
        /// --build spec.json salida.dtsx : genera el paquete SSIS desde la consola (util para pruebas/CI).
        /// --ddl spec.json               : imprime el DDL.
        /// --validate spec.json          : valida el diseno sin tocar bases de datos.
        /// --reconcile spec.json         : compara el DW cargado contra el origen (codigo 1 si no coincide).
        /// --check [spec.json]           : comprueba los requisitos de este equipo (SSIS, MSOLEDBSQL, DSN, .NET).
        /// --inspect paquete.dtsx spec.json : prueba de humo del paquete generado (sin SSIS ni bases de datos).
        /// --all ... [--recreate] [--notify https://webhook] : el aviso final (OK o FALLO) se envia al webhook.
        /// </summary>
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool AttachConsole(int dwProcessId);

        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0].StartsWith("--")) AttachConsole(-1);   // app de ventanas: engancha la consola del que la invoca
            AppDomain.CurrentDomain.UnhandledException += (o, e) => AppLog.Error("Excepcion no controlada", e.ExceptionObject as Exception);
            if (args.Length >= 1 && args[0] == "--check")
            {
                DwSpec cs = null; try { if (args.Length > 1) cs = DwSpec.FromJson(File.ReadAllText(args[1])); } catch { }
                var rs = Requirements.Check(cs == null ? null : cs.SourceDsn);
                Console.WriteLine(Requirements.Format(rs));
                return rs.All(r => r.Ok || r.Name.Contains("Excel")) ? 0 : 1;
            }
            if (args.Length >= 3 && args[0] == "--inspect")
            {
                try
                {
                    var probs = PackageInspector.Verify(DwSpec.FromJson(File.ReadAllText(args[2])), PackageInspector.Read(File.ReadAllText(args[1])));
                    foreach (var p in probs) Console.Error.WriteLine("  - " + p);
                    Console.WriteLine(probs.Count == 0 ? "Paquete conforme." : probs.Count + " problemas.");
                    return probs.Count == 0 ? 0 : 1;
                }
                catch (Exception ex) { Console.Error.WriteLine("ERROR: " + ex.Message); return 1; }
            }
            if (args.Length >= 2 && args[0] == "--ddl")
            {
                Console.OutputEncoding = System.Text.Encoding.UTF8;
                Console.WriteLine(DdlGenerator.Tables(DwSpec.FromJson(File.ReadAllText(args[1])))); return 0;
            }
            if (args.Length >= 2 && args[0] == "--ask")
            {
                // Prueba rapida del motor de IA con Claude Code (suscripcion): EtlStudio.exe --ask "hola"
                try
                {
                    var exe = ClaudeCodeClient.FindExe();
                    if (exe == null) throw new Exception("No se encontro Claude Code.");
                    Console.OutputEncoding = System.Text.Encoding.UTF8;
                    Console.WriteLine("Claude Code: " + exe);
                    Console.WriteLine(new ClaudeCodeClient(exe).Send("Responde de forma breve.", new[] { new ClaudeClient.Turn { Role = "user", Text = args[1] } }));
                    return 0;
                }
                catch (Exception ex) { Console.Error.WriteLine("ERROR: " + ex.Message); return 1; }
            }
            if (args.Length >= 2 && args[0] == "--readdoc")
            {
                Console.OutputEncoding = System.Text.Encoding.UTF8;
                var a = DocReader.Load(args[1]);
                Console.WriteLine("[" + a.Kind + "] " + a.Summary);
                if (a.Text != null) Console.WriteLine(a.Text.Length > 1500 ? a.Text.Substring(0, 1500) + "\n..." : a.Text);
                return 0;
            }
            if (args.Length >= 3 && args[0] == "--plan")
            {
                // Ejecuta un plan (JSON de Claude o escrito a mano) sin ventana: DSN -> DW -> SSIS -> carga -> verificacion -> reporte .docx
                try
                {
                    var plan = Plan.FromJson(File.ReadAllText(args[1]));
                    var errs = plan.Spec.Validate(); if (errs.Count > 0) throw new Exception(string.Join("; ", errs));
                    var r = Pipeline.Run(plan, args.Length > 3 && args[3] == "--recreate", args[2],
                        (id, st) => Console.WriteLine("[" + st + "] " + id), m => Console.WriteLine("    " + m));
                    Console.WriteLine("Reporte: " + r.ReportPath); return 0;
                }
                catch (Exception ex) { Console.Error.WriteLine("ERROR: " + ex.Message); return 1; }
            }
            if (args.Length >= 2 && args[0] == "--validate")
            {
                // Valida el diseno sin tocar ninguna base de datos: EtlStudio.exe --validate spec.json
                try
                {
                    var errs = DwSpec.FromJson(File.ReadAllText(args[1])).Validate();
                    foreach (var e in errs) Console.Error.WriteLine("  - " + e);
                    Console.WriteLine(errs.Count == 0 ? "Diseno valido." : errs.Count + " problemas.");
                    return errs.Count == 0 ? 0 : 1;
                }
                catch (Exception ex) { Console.Error.WriteLine("ERROR: " + ex.Message); return 1; }
            }
            if (args.Length >= 2 && args[0] == "--reconcile")
            {
                // Compara el DW ya cargado contra el origen: EtlStudio.exe --reconcile spec.json  (codigo 1 si no coincide)
                try
                {
                    var s = DwSpec.FromJson(File.ReadAllText(args[1]));
                    var errs = s.Validate(); if (errs.Count > 0) throw new Exception(string.Join("; ", errs));
                    var r = Reconciliation.Run(s);
                    Console.WriteLine(SourceReader.SampleRows(r.ToTable(), 50));
                    Console.WriteLine(r.Summary());
                    return r.Ok ? 0 : 1;
                }
                catch (Exception ex) { Console.Error.WriteLine("ERROR: " + ex.Message); return 1; }
            }
            if (args.Length >= 3 && args[0] == "--all")
            {
                // Flujo completo sin ventana (el mismo Pipeline que usa la interfaz): perfilar -> DW -> paquete -> validar -> cargar -> verificar y reconciliar -> reporte.
                try
                {
                    var s = DwSpec.FromJson(File.ReadAllText(args[1]));
                    var errs = s.Validate(); if (errs.Count > 0) throw new Exception(string.Join("; ", errs));
                    var pkgPath = Path.GetFullPath(args[2]);
                    int ni = Array.IndexOf(args, "--notify"); string notify = ni >= 0 && ni + 1 < args.Length ? args[ni + 1] : null;
                    PipelineResult r;
                    try { r = Pipeline.Run(new Plan { Titulo = s.DwName, Spec = s }, args.Length > 3 && args[3] == "--recreate", Path.GetDirectoryName(pkgPath),
                        (id, st) => Console.WriteLine("[" + st + "] " + id), m => Console.WriteLine("    " + m), pkgPath); }
                    catch (Exception ex)
                    {
                        if (notify != null) { var err = Notifier.Send(notify, s.DwName, false, ex.Message, null); if (err != null) Console.Error.WriteLine("Aviso no enviado: " + err); }
                        throw;
                    }
                    if (notify != null) { var err = Notifier.Send(notify, s.DwName, true, "Carga y reconciliacion correctas.", r.ReportPath); if (err != null) Console.Error.WriteLine("Aviso no enviado: " + err); }
                    foreach (var kv in r.Checks)
                    {
                        Console.WriteLine("== " + kv.Key.Title);
                        Console.WriteLine(SourceReader.SampleRows(kv.Value, 30));
                    }
                    Console.WriteLine("Reporte: " + r.ReportPath); return 0;
                }
                catch (Exception ex) { Console.Error.WriteLine("ERROR: " + ex.Message); return 1; }
            }
            if (args.Length >= 3 && args[0] == "--build")
            {
                try { Console.WriteLine("Paquete generado: " + new SsisBuilder(DwSpec.FromJson(File.ReadAllText(args[1]))).Build(args[2])); return 0; }
                catch (Exception ex) { Console.Error.WriteLine("ERROR: " + ex.Message); return 1; }
            }
            var app = new Application();
            app.DispatcherUnhandledException += (s, e) => { MessageBox.Show(e.Exception.Message, "EtlStudio", MessageBoxButton.OK, MessageBoxImage.Error); e.Handled = true; };
            return app.Run(new MainWindow());
        }
    }
}
