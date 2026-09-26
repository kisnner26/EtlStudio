using System;
using System.IO;
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
        /// </summary>
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool AttachConsole(int dwProcessId);

        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0].StartsWith("--")) AttachConsole(-1);   // app de ventanas: engancha la consola del que la invoca
            if (args.Length >= 2 && args[0] == "--ddl")
            {
                Console.OutputEncoding = System.Text.Encoding.UTF8;
                Console.WriteLine(DdlGenerator.Tables(DwSpec.FromJson(File.ReadAllText(args[1])))); return 0;
            }
            if (args.Length >= 3 && args[0] == "--all")
            {
                // Flujo completo sin ventana: crear DW -> generar paquete -> validar -> ejecutar -> verificar.
                try
                {
                    var s = DwSpec.FromJson(File.ReadAllText(args[1]));
                    var errs = s.Validate(); if (errs.Count > 0) throw new Exception(string.Join("; ", errs));
                    bool recreate = args.Length > 3 && args[3] == "--recreate";
                    if (recreate) SqlRunner.Exec(s.SqlServer, "master", DdlGenerator.DropDatabase(s));
                    SqlRunner.Exec(s.SqlServer, "master", DdlGenerator.CreateDatabase(s));
                    SqlRunner.Exec(s.SqlServer, s.DwName, DdlGenerator.Tables(s));
                    Console.WriteLine("DW listo. Generando paquete...");
                    new SsisBuilder(s).Build(args[2]);
                    if (DtExec.Run(args[2], true, l => { })!= 0) throw new Exception("Fallo la validacion del paquete");
                    Console.WriteLine("Paquete valido. Ejecutando carga...");
                    if (DtExec.Run(args[2], false, l => { if (l.Contains("Error") || l.Contains("Description")) Console.WriteLine(l); }) != 0) throw new Exception("Fallo la ejecucion");
                    foreach (var c in Verification.For(s))
                    {
                        Console.WriteLine("== " + c.Title);
                        Console.WriteLine(EtlStudio.Core.SourceReader.SampleRows(SqlRunner.Query(s.SqlServer, s.DwName, c.Sql), 6));
                    }
                    return 0;
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
