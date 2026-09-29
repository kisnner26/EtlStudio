# Solucion de problemas

Primero corre `EtlStudio.exe --check` (opcionalmente con tu `spec.json`): lista lo que falta en el equipo y como resolverlo.
El registro de la aplicacion esta en `%APPDATA%\EtlStudio\logs\etlstudio.log` (rota a 1 MB, conserva 5 archivos, no guarda tokens).

| Sintoma | Causa probable | Que hacer |
|---|---|---|
| `Falta el token de acceso en el DSN` | El DSN existe pero sin token (o esta vencido) | Administrador ODBC de 64 bits > DSN de usuario > Configure > pega el token en *Password* > OK. Es un paso unico por equipo. |
| `No existe el DSN ...` | El DSN no esta creado y el enunciado no trae host/HTTP path | Crealo a mano en el administrador ODBC de 64 bits. |
| `No se encontro DTExec.exe` / falla al generar el paquete | Falta SSIS 2022 | Instala SQL Server Integration Services 2022 o la extension SSIS de Visual Studio. `--check` lo confirma. |
| `El origen no cumple lo que exige el modelo` | Nulos en llaves, fechas o medidas; origen vacio; fechas fuera de `Dim_Tiempo` | Corrige el origen o filtralo en el `SELECT`. Para nulos en columnas de dimension puedes usar `"NullPolicy": "Unknown"` (se cargan como `Desconocido`). Para fechas, amplia `Time.From`/`Time.To`. |
| `dtexec devolvio el codigo 1 (carga)` con `no match` en un Lookup | Una fila del origen no encuentra su dimension | La carga se detiene a proposito para no perder hechos. El DW **no se toca**: la carga escribe en `stg` y solo publica si todo va bien. Revisa mayusculas/acentos y define una `NaturalKey` en la dimension. |
| `Los datos cargados no coinciden con el origen` | La reconciliacion encontro diferencias | Lee la seccion *Reconciliacion* del reporte `.docx`: dice que metrica difiere (filas, llaves, fechas, suma de una medida). |
| Caracteres `?` en lugar de acentos o simbolos | Columna `VARCHAR` (pagina de codigos 1252) | Usa `NVARCHAR(n)` en el tipo de esa columna. |
| El DW quedo con datos viejos tras un error | Es lo esperado: el destino solo cambia al publicar | Corrige la causa y vuelve a ejecutar. Las tablas `stg.*` que queden pueden borrarse; se recrean en cada corrida. |
| `ETL_Auditoria` muestra `Fallido` | La corrida anterior se cayo (queda `EnCurso` y la siguiente la cierra como `Fallido`) | Revisa el log y el reporte de esa corrida. |
| Metas de Excel: `Microsoft.ACE.OLEDB.16.0` no registrado | Falta Access Database Engine 64 bits | Instala *Microsoft Access Database Engine 2016 Redistributable* (64 bits). |
