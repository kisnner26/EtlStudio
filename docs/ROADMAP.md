# Estado de la hoja de ruta (version 1.4.1)

**Evidencia real (Windows, `--all --recreate`, Databricks, 3M filas):** exit 0; validacion y carga con `dtexec` correctas (94 s); reconciliacion de 11 comprobaciones OK (filas, llaves, fechas, 4 sumas, 29 periodos, metas de Excel). Ese diseno uso solo los valores por defecto (`Full`, `Fail`, sin llave natural ni `NVARCHAR`). Lo demas, marcado "sin verificar", sigue sin ejecutarse.


Leyenda: **Hecho** = implementado y probado con `tests/run.sh`; **Hecho, sin verificar en Windows** = implementado pero depende de SSIS/WPF, que no se pudieron ejecutar; **Pendiente** = no implementado.

| # | Tarea | Estado |
|---|---|---|
| 1 | Ejecutar `--all` en Windows y archivar evidencia | **Hecho** para el modo por defecto (ver arriba). Pendiente repetirlo con `samples/autos_incremental.json` |
| 2 | Prueba de humo del paquete | **Hecho y verificado con un `.dtsx` real** (se corrigio un falso positivo); el `Pipeline` la ejecuta solo |
| 3 | Confirmar el workflow de Actions | **Pendiente** (se corre al hacer push; las pruebas pasan localmente con Mono) |
| 4 | Regresion con datos con defecto | **Hecho** (parte pura: perfil y reconciliacion). La ejecucion real contra datos malos sigue pendiente (#1) |
| 5 | Tablas de paso y publicacion transaccional | **Hecho, sin verificar en Windows** (SQL probado como texto; el paquete no se ejecuto) |
| 6 | Tabla de rechazos `ETL_Rechazos` | **Hecho, sin verificar en Windows ni contra SQL Server** (`RejectPolicy: Reject`) |
| 7 | Miembro "Desconocido" y politica de nulos | **Hecho** (`NullPolicy`). Aplica a columnas de dimension; nulos en llave, fecha o medidas siguen deteniendo |
| 8 | Carga incremental con marca de agua | **Hecho, sin verificar en Windows ni contra SQL Server** (`LoadMode: Incremental`; no detecta cambios ni borrados en filas ya cargadas) |
| 9 | Lookup por llave natural | **Hecho, sin verificar en Windows** (`NaturalKey`) |
| 10 | Unicode | **Hecho, sin verificar en Windows** (`NVARCHAR`, `DT_WSTR`) |
| 11 | Dimensiones tipo 2 | **Pendiente** |
| 12 | Atributos derivados en dimensiones | **Hecho** (`Computed` por dimension; el DDL se probo como texto) |
| 13 | Varias tablas de hechos y dimensiones conformadas | **Pendiente** |
| 14 | Abstraer el origen | **Hecho parcialmente**: `SourceKind` cambia la vista previa y los textos. El paquete sigue leyendo por ODBC (ADO NET) y el perfilado usa SQL comun; PostgreSQL y SQL Server no se probaron |
| 15 | Probar el SQL de perfilado contra Databricks | **Hecho** (perfil, agregados y periodos corrieron contra Databricks) |
| 16 | Reconciliacion por contenido | **Hecho parcialmente**: comparacion por anio-mes (filas y sumas). No hay hash fila a fila (no existe una funcion de hash portable entre motores) |
| 17 | Parametros de proyecto SSIS | **Pendiente** |
| 18 | Proveedor de log dentro del paquete | **Hecho parcialmente**: todas las lineas de `dtexec` van al registro de la app. Un proveedor de log dentro del `.dtsx` no se implemento (no se puede validar la API de SSIS aqui) |
| 19 | Comprobacion de requisitos | **Hecho, sin verificar en Windows** (`--check`; usa el registro de Windows) |
| 20 | Registro rotativo de la app | **Hecho** (`AppLog`, probado). Se registran los comandos de consola y el `Pipeline`; la interfaz WPF no se cambio |
| 21 | Notificacion por webhook | **Hecho** (`--notify`; el envio real no se probo) |
| 22 | Ejecucion programada | **Hecho, sin verificar en Windows** (`tools\schedule-task.ps1`) |
| 23 | API de Anthropic para distribucion | **Pendiente** (ya existe el modo `api` en Ajustes) |
| 24 | Dividir `MainWindow.cs` | **Pendiente** (no se puede compilar WPF aqui; un refactor a ciegas seria irresponsable) |
| 25 | Guia de solucion de problemas | **Hecho** |
| 26 | Diagramas de arquitectura | **Hecho** |
| 27 | Releases | **Hecho, sin verificar** (workflow al publicar una etiqueta `v*`; el instalador se adjunta a mano) |
| 28 | Politica de revision del plan de la IA | **Hecho** como politica documentada (docs/ARQUITECTURA.md); no hay un bloqueo tecnico |
