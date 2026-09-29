# Cambios

## 1.2.0

Fiabilidad de la carga y del diseno.

- **Reconciliacion origen vs destino** (nuevo `Reconciliation.cs`): filas, llaves de negocio distintas, fecha minima y maxima y suma de cada medida. Se ejecuta al final del proceso completo, en la pagina Verificar y con `--reconcile`. Si no cuadra, el proceso falla, pero el reporte se escribe igual.
- **Perfilado del origen antes de crear nada**: se detiene si hay nulos en columnas que el modelo exige, si el origen esta vacio, si falta el Excel de metas o si hay fechas fuera de `Dim_Tiempo`.
- **Los lookups de la Fact fallan ante una llave sin dimension.** Antes redirigian la fila a una salida sin conectar y se descartaba sin aviso. Las metas de Excel siguen redirigiendo (filas en blanco) y se reconcilian contra el archivo.
- **`SK_Tiempo` se calcula con YEAR/MONTH/DAY sobre la fecha real**, ya no cortando el texto de la fecha.
- **Auditoria** en `dbo.ETL_Auditoria`: inicio y fin de cada corrida dentro del paquete.
- **El paquete se guarda con `DontSaveSensitive`**: abre en cualquier equipo. Las conexiones se llaman como la base y el DSN (antes `localhost.<base>` y `<dsn>.token`), y se quito el `ROWS_PER_BATCH = 60` fijo de las metas.
- **Validacion del diseno mas estricta** (`DwSpec.Validate`): fechas de `Time` en `yyyy-MM-dd` y con tope de 100 anios, tipos dentro de rango (`VARCHAR` 1-8000, `DECIMAL` p<=38 y s<=p), medidas numericas, nombres reservados y repetidos, columnas calculadas restringidas a medidas y funciones permitidas, sin comentarios en el `SELECT` de origen, y cadenas de conexion (SQL Server, DSN, Excel) sin caracteres de inyeccion.
- `--all` ahora usa el mismo `Pipeline` que la interfaz (antes duplicaba los pasos). Nuevos `--validate` y `--reconcile`.
- **Pruebas** del nucleo (`tests/`, 76 comprobaciones) e integracion continua.

## 1.1.0

Version inicial publicada.
