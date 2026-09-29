# Estado de la hoja de ruta (version 1.3.0)

Leyenda: **Hecho** = implementado y probado con `tests/run.sh`; **Hecho, sin verificar en Windows** = implementado pero depende de SSIS/WPF, que no se pudieron ejecutar; **Pendiente** = no implementado.

| # | Tarea | Estado |
|---|---|---|
| 1 | Ejecutar `--all` en Windows y archivar evidencia | **Pendiente** (requiere Windows, SSIS y Databricks) |
| 2 | Prueba de humo del paquete | **Hecho**: `--inspect` y `PackageInspector`. Probado con un XML sintetico segun el formato SSIS 2012+, **no** con un `.dtsx` real: al primer paquete real puede requerir ajuste |
| 3 | Confirmar el workflow de Actions | **Pendiente** (se corre al hacer push; las pruebas pasan localmente con Mono) |
| 4 | Regresion con datos con defecto | **Hecho** (parte pura: perfil y reconciliacion). La ejecucion real contra datos malos sigue pendiente (#1) |
| 5 | Tablas de paso y publicacion transaccional | **Hecho, sin verificar en Windows** (SQL probado como texto; el paquete no se ejecuto) |
| 6 | Tabla de rechazos `ETL_Rechazos` | **Pendiente** (hoy una llave sin dimension detiene la carga) |
| 7 | Miembro "Desconocido" y politica de nulos | **Hecho** (`NullPolicy`). Aplica a columnas de dimension; nulos en llave, fecha o medidas siguen deteniendo |
| 8 | Carga incremental con marca de agua | **Pendiente** |
| 9 | Lookup por llave natural | **Hecho, sin verificar en Windows** (`NaturalKey`) |
| 10 | Unicode | **Hecho, sin verificar en Windows** (`NVARCHAR`, `DT_WSTR`) |
| 11 | Dimensiones tipo 2 | **Pendiente** |
| 12 | Atributos derivados en dimensiones | **Pendiente** |
| 13 | Varias tablas de hechos y dimensiones conformadas | **Pendiente** |
| 14 | Abstraer el origen | **Pendiente** |
| 15 | Probar el SQL de perfilado contra Databricks | **Pendiente** (requiere Databricks) |
| 16 | Reconciliacion por contenido | **Pendiente** |
| 17 | Parametros de proyecto SSIS | **Pendiente** |
| 18 | Proveedor de log dentro del paquete | **Pendiente** |
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
