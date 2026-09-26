using System.Collections.Generic;
using System.Linq;

namespace EtlStudio.Core
{
    public class Check { public string Title, Explain, Sql; }

    /// <summary>Consultas de verificacion comentadas, generadas desde el spec (todas de solo lectura).</summary>
    public static class Verification
    {
        public static List<Check> For(DwSpec s)
        {
            var f = s.Fact; var t = s.Time.Name; var l = new List<Check>();

            l.Add(new Check
            {
                Title = "1) Conteo de tablas",
                Explain = "Cuenta las filas de cada tabla del DW. Las dimensiones deben tener los valores distintos del origen y la Fact tantas filas como el origen.",
                Sql = "SELECT '" + t + "' AS Tabla, COUNT(*) AS Total_Registros FROM dbo." + t + "\n" +
                      string.Concat(s.Dims.Select(d => "UNION ALL SELECT '" + d.Name + "', COUNT(*) FROM dbo." + d.Name + "\n")) +
                      "UNION ALL SELECT '" + f.Name + "', COUNT(*) FROM dbo." + f.Name +
                      (s.Goals != null ? "\nUNION ALL SELECT '" + s.Goals.Table + "', COUNT(*) FROM dbo." + s.Goals.Table : "")
            });

            var firstDim = s.Dims[0];
            l.Add(new Check
            {
                Title = "2) Hechos por " + firstDim.Name,
                Explain = "Une la Fact con las dimensiones. Si se perdieron filas en los joins, la suma de 'Filas' no coincide con el total de la Fact.",
                Sql = "SELECT d." + firstDim.Columns[0].Dst + ", COUNT(*) AS Filas, MIN(t.Fecha) AS Primera_Fecha, MAX(t.Fecha) AS Ultima_Fecha\n" +
                      "FROM dbo." + f.Name + " f\nJOIN dbo." + firstDim.Name + " d ON d." + firstDim.Key + " = f." + firstDim.Key + "\n" +
                      "JOIN dbo." + t + " t ON t.SK_Tiempo = f.SK_Tiempo\nGROUP BY d." + firstDim.Columns[0].Dst + "\nORDER BY 1"
            });

            l.Add(new Check
            {
                Title = "3) Filas huerfanas (esperado: 0 en todas)",
                Explain = "Cuenta hechos sin dimension asociada. Cualquier valor distinto de 0 indica un problema de llaves.",
                Sql = "SELECT " + string.Join(",\n       ", new[] { "(SELECT COUNT(*) FROM dbo." + f.Name + " f LEFT JOIN dbo." + t + " d ON d.SK_Tiempo = f.SK_Tiempo WHERE d.SK_Tiempo IS NULL) AS Sin_Tiempo" }
                        .Concat(s.Dims.Select(d => "(SELECT COUNT(*) FROM dbo." + f.Name + " f LEFT JOIN dbo." + d.Name + " d ON d." + d.Key + " = f." + d.Key + " WHERE d." + d.Key + " IS NULL) AS Sin_" + d.Name.Replace("Dim_", ""))))
            });

            l.Add(new Check
            {
                Title = "4) Duplicados de la llave de negocio (esperado: 0 filas)",
                Explain = "Cada registro del origen debe aparecer una sola vez en la Fact.",
                Sql = "SELECT " + f.BusinessKey.Dst + ", COUNT(*) AS Veces FROM dbo." + f.Name + " GROUP BY " + f.BusinessKey.Dst + " HAVING COUNT(*) > 1"
            });

            l.Add(new Check
            {
                Title = "5) Muestra de la Fact",
                Explain = "Primeras filas con columnas calculadas (las calcula SQL Server) y fecha de carga (auditoria).",
                Sql = "SELECT TOP 5 * FROM dbo." + f.Name + " ORDER BY SK_Fact"
            });

            if (s.Goals != null)
            {
                var g = s.Goals; var gd = s.Dims.First(d => d.Name == g.DimName);
                l.Add(new Check
                {
                    Title = "6) Metas cargadas desde Excel",
                    Explain = "Las llaves (SK_) se traducen a nombres. Compara con el Excel: mismas filas y mismos montos.",
                    Sql = "SELECT d." + gd.Columns[0].Dst + ", t.Fecha, m.Monto_Meta\nFROM dbo." + g.Table + " m\nJOIN dbo." + gd.Name + " d ON d." + gd.Key + " = m." + gd.Key + "\n" +
                          "JOIN dbo." + t + " t ON t.SK_Tiempo = m.SK_Tiempo\nORDER BY t.Fecha, d." + gd.Columns[0].Dst
                });
                l.Add(new Check
                {
                    Title = "7) Suma de metas (compara con =SUMA() del Excel)",
                    Explain = "La suma debe ser identica a la del archivo de Excel.",
                    Sql = "SELECT COUNT(*) AS Filas, SUM(Monto_Meta) AS Suma_Metas FROM dbo." + g.Table
                });
            }
            return l;
        }
    }
}
