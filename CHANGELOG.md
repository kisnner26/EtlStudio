# Cambios

## 1.4.2

Segunda corrida real en Windows (`samples/autos_incremental.json`, dos corridas, con una fila fabricada de fecha 1999): exit 0 en ambas. Verificado: carga incremental (2.a corrida: marca de agua 2026-05-01, 13 s frente a 93 s, 3.000.000 hechos en las dos, sin duplicados), rechazo con motivo correcto (`ETL_Rechazos` = 1, no entra a la Fact), reconciliacion cargadas + rechazadas = origen (3.000.001), llave natural, atributo derivado `Inicial_Marca`, y dimensiones estables entre corridas.

- **Corregido: un miembro que solo usa una fila rechazada quedaba huerfano en la dimension** (la corrida mostro `Dim_Vehiculo` con 2.001 miembros: el `VIN-REJECT-TEST` de la fila rechazada). Con `RejectPolicy: Reject`, `SQL_Publicar` ahora cruza primero y solo agrega a las dimensiones los miembros que usa alguna fila aceptada. **Este cambio no se ha ejecutado contra SQL Server.**
- Limites documentados de la carga incremental: no recoge filas nuevas con fecha anterior a la marca de agua, ni cambios o borrados de filas ya cargadas, y una fila rechazada con fecha anterior a la marca no se reintenta.

## 1.4.1

Correcciones tras la primera corrida real en Windows (`--all --recreate` contra Databricks, 3.000.000 de filas y metas de Excel: exit 0, reconciliacion correcta).

- **Faltaba la referencia `System.Xml.Linq` en `EtlStudio.csproj`** (la usa `PackageInspector`): 1.3.0 y 1.4.0 no compilaban en Windows.
- **`PackageInspector` daba un falso positivo con un paquete real**: SSIS guarda la expresion de `SK_Tiempo` como `[YEAR](...)` (funciones entre corchetes). Ahora se normaliza y se prefiere `FriendlyExpression`. Probado contra el `.dtsx` real de la corrida.
- **El `Pipeline` ahora corre esa prueba de humo sobre el paquete que genera**, antes de `dtexec`. Antes solo existia como comando `--inspect`.

Evidencia de la corrida (modo `Full`, `Fail`): staging, fusion de dimensiones, publicacion transaccional, reconciliacion por periodo (29 periodos), y SQL de perfilado contra Databricks. **No se ejercitaron** `Reject`, `Incremental`, `NullPolicy: Unknown`, `NaturalKey`, `NVARCHAR` ni `Computed` en dimensiones.

## 1.4.0

- **Rechazos** (`RejectPolicy`: `Fail` | `Reject`): con `Reject`, una fila sin dimension o con fecha fuera de `Dim_Tiempo` no detiene la carga; se guarda con su motivo en `dbo.ETL_Rechazos` y se carga el resto. La Fact se aterriza cruda en `stg` y `SQL_Publicar` la cruza con las dimensiones (sin multiplicar filas si la llave no es unica). La reconciliacion cuenta cargadas + rechazadas y avisa.
- **Carga incremental** (`LoadMode`: `Full` | `Incremental`): lee solo las filas con fecha >= la ultima cargada (marca de agua calculada por el `Pipeline`), agrega miembros nuevos a las dimensiones conservando sus llaves y no vuelve a cargar hechos ya existentes (por llave de negocio). Nuevo paso `SQL_Fusionar_Dimensiones` y tablas de aterrizaje `stg.<Dim>_Nuevos`.
- **Atributos derivados en dimensiones** (`Computed` en cada dimension): columnas persistidas con una expresion sobre las columnas de la dimension (funciones de texto y `CASE`, sin subconsultas).
- **Reconciliacion por periodo**: ademas de los totales, compara filas y sumas por anio-mes (detecta errores que se compensan).
- **Origen configurable** (`SourceKind`: Databricks, PostgreSQL, SqlServer, Generic) para la vista previa y los textos del reporte.
- Todas las lineas de `dtexec` quedan en el registro de la aplicacion.
- `samples/autos_incremental.json` usa todas las opciones. Pruebas: 184 comprobaciones.

**Sin verificar en Windows**: igual que 1.3.0, el generador SSIS (nuevo paso, aterrizaje de dimensiones y Fact cruda) no se ejecuto. El SQL de publicacion, fusion y reconciliacion esta probado solo como texto, **no contra un SQL Server**.

**Compatibilidad**: el paquete agrega `SQL_Fusionar_Dimensiones` y las dimensiones se cargan en `stg.<Dim>_Nuevos`; regenera los `.dtsx`.
## 1.3.0

Carga que no destruye el DW, datos reales sucios y operacion.

- **Carga por tablas de paso (esquema `stg`) y publicacion transaccional.** Se quito `SQL_Limpiar_Destino`. La carga escribe en `stg.*` y `SQL_Publicar` pasa todo a `dbo` en una sola transaccion (con `IDENTITY_INSERT` para conservar las llaves): si algo falla, el DW queda como estaba.
- **Llave natural por dimension** (`NaturalKey`, opcional): el Lookup busca solo por ella (no por todas las columnas), la dimension recibe un indice `UNIQUE` y se puebla una fila por llave.
- **Politica de nulos** (`NullPolicy`: `Fail` por defecto | `Unknown`): con `Unknown`, un nulo en una columna de dimension se carga como `Desconocido` en lugar de detener el proceso.
- **Unicode**: se admite `NVARCHAR(1..4000)` en dimensiones y llave de negocio (`DT_WSTR` en el paquete).
- **Requisitos al arrancar**: `--check` comprueba .NET, SSIS, MSOLEDBSQL, ACE y el DSN, e indica como resolver cada faltante.
- **Registro de la aplicacion** rotativo en `%APPDATA%\EtlStudio\logs` (sin tokens). **Aviso de resultado** a un webhook HTTPS con `--all ... --notify <url>`.
- **`--inspect paquete.dtsx spec.json`**: prueba de humo del paquete generado (orden de tareas, `ProtectionLevel`, `NoMatchBehavior` de los lookups de la Fact y expresion de `SK_Tiempo`), sin SSIS ni bases de datos.
- `tools/schedule-task.ps1` (ejecucion programada), flujo de release, `docs/ARQUITECTURA.md`, `docs/SOLUCION_DE_PROBLEMAS.md` y `docs/ROADMAP.md` con el estado de cada punto.
- Pruebas: de 76 a 127 comprobaciones (staging, publicacion, Unicode, llave natural, politica de nulos, regresion con datos con defecto, inspector, registro, avisos).

**Cambios que rompen compatibilidad**: el paquete ya no tiene `SQL_Limpiar_Destino`; los `.dtsx` generados con 1.2.0 deben regenerarse. Los lookups leen de `stg`, por lo que el paquete necesita las tablas `stg.*` para validarse (el proceso completo las crea).

**Sin verificar en Windows**: `SsisBuilder` (staging, llave natural, `DT_WSTR`), `Program` y la interfaz no se compilaron ni ejecutaron aqui. Ejecuta `--all` con `--recreate` y luego `--inspect` antes de darlo por bueno.

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
