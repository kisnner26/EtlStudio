# EtlStudio

App de escritorio para Windows que **replica el proceso de armar un Data Warehouse en SQL Server y su paquete SSIS** a partir de un origen ODBC (pensada para Databricks), con **Claude** como asistente.

```
Origen Databricks (ODBC)  ->  DW en estrella (SQL Server)  +  paquete SSIS (.dtsx)  ->  verificacion
```

## Modo automatico (pantalla de Inicio)

Suelta tu enunciado (**PPTX, DOCX, PDF, XLSX, capturas, TXT/SQL**) o descríbelo con un prompt y pulsa **Resolver automaticamente**:

1. Se lee el contenido de los archivos (los tokens y contraseñas se ocultan antes de enviar texto a la IA).
2. Claude arma un **plan** (conexion, diseño en estrella, pasos, advertencias) y, si el diseño no valida, lo corrige una vez.
3. Tu **apruebas** el plan con un clic (o lo editas en *Diseño*).
4. EtlStudio conecta, crea el DW, genera el paquete SSIS, lo valida, carga, verifica y crea un **reporte .docx** con la evidencia.

Si el DSN de Databricks no existe, se crea con el host y el HTTP path del enunciado; **el token lo pegas tu una sola vez** en el dialogo del driver ODBC (la app nunca lo escribe).

## Instalar

Descarga/compila `dist\EtlStudio-Setup.exe` y ejecutalo: instala solo para tu usuario (sin administrador), crea los accesos directos y se registra en *Aplicaciones instaladas*. Instalacion silenciosa: `EtlStudio-Setup.exe /S`.

## Que hace (paginas avanzadas)

1. **Ajustes**: SQL Server (autenticacion de Windows), DSN ODBC del origen y tu API key de Anthropic.
2. **Fuente**: escribes el `SELECT` de origen y ves esquema y filas de muestra.
3. **Diseno (IA)**: Claude propone el modelo en estrella (dimensiones, hechos, medidas, columnas calculadas) como JSON editable. Tu lo revisas y validas; nada se crea sin tu accion.
4. **Ejecutar**: crea la base y las tablas (DDL idempotente), **genera un paquete `.dtsx` real** con el modelo de objetos de SSIS, lo valida y lo ejecuta con `dtexec`.
5. **Verificar**: consultas de solo lectura y comentadas (conteos, huerfanos, duplicados, metas de Excel) y explicacion de los resultados por Claude.
6. **Asistente**: chat con Claude que conoce tu diseno y el log para diagnosticar errores.

El paquete generado sigue el patron del curso: `SQL_Limpiar_Destino` -> `DFT_Poblar_Dimensiones` (ADO NET -> Conversion de datos -> OLE DB) -> `DFT_Extraer_Origen` (ADO NET -> Conversion de datos a `DT_STR` -> Lookups por dimension -> Columna derivada `SK_Tiempo` -> carga rapida) -> `DFT_Cargar_Metas_Excel` (opcional).

## Requisitos

- Windows 10/11 x64, .NET Framework 4.7.2+
- SQL Server (cualquier edicion) y el proveedor **OLE DB Driver for SQL Server (MSOLEDBSQL)**
- **Runtime de SSIS 2022 (v16)**: `dtexec` en `C:\Program Files\Microsoft SQL Server\160\DTS\Binn` y los ensamblados `Microsoft.SqlServer.ManagedDTS`, `DTSPipelineWrap` y `DTSRuntimeWrap` en el GAC (vienen con SQL Server Integration Services o con la extension SSIS de Visual Studio)
- Driver ODBC del origen (p. ej. Databricks ODBC / Simba) y un DSN configurado; el token vive en el DSN, no en esta app
- Para metas desde Excel: proveedor `Microsoft.ACE.OLEDB.16.0` (64 bits)
- **Claude**, de una de estas dos formas (se elige en *Ajustes*):
  - **Tu suscripcion de Claude, vía Claude Code** (por defecto, sin API key ni crédito de API): la app ejecuta el `claude` instalado con tu propia sesión (`claude -p`). Inicia sesión una vez desde *Ajustes → Iniciar sesión de Claude*. El consumo sale de tu plan.
  - **API de Anthropic** (de pago, con API key y saldo): https://console.anthropic.com

## Compilar

```powershell
.\build.ps1          # compila la app y crea dist\EtlStudio-Setup.exe
.\bin\Release\EtlStudio.exe
```

`build.ps1` busca MSBuild de Visual Studio automaticamente. El icono se genera por codigo con `tools\make-icon.ps1` (ya incluido en `assets\`). Tambien puedes abrir `EtlStudio.csproj` directamente en Visual Studio.

Notas para contribuir: los fuentes son UTF-8 con BOM; edita con un editor que respete la codificacion.

## Linea de comandos (pruebas / CI)

```powershell
EtlStudio.exe --ddl   samples\autos_databricks.json
EtlStudio.exe --build samples\autos_databricks.json salida.dtsx
EtlStudio.exe --all   samples\autos_databricks.json salida.dtsx [--recreate]   # crea DW, genera, valida, ejecuta y verifica
```

## Seguridad

- La API key se guarda **cifrada con DPAPI** (solo tu usuario de Windows) en `%APPDATA%\EtlStudio\settings.json`, o se lee de `ANTHROPIC_API_KEY`. Nunca la subas al repositorio.
- El token de Databricks queda en el DSN de Windows; la app no lo ve.
- El JSON que propone la IA se **valida antes de usarse**: identificadores `[A-Za-z0-9_]`, lista blanca de tipos SQL, expresiones calculadas restringidas y `SELECT` de origen sin `;`. Aun asi, **revisa el diseno** antes de crear.
- "Recrear la base" borra la base existente y pide confirmacion.
- A Claude solo se le envian el esquema, hasta 8 filas de muestra, el diseno y las ultimas lineas del log.

## Limitaciones conocidas

- El paquete usa el runtime de SSIS 2022 (v16); Visual Studio 2026 puede pedir actualizar el formato al abrirlo.
- Las columnas de dimension y la llave de negocio son `VARCHAR` (se convierten a `DT_STR`, pagina 1252).
- Un modelo en estrella con una tabla de hechos; dimensiones de cambio lento (SCD) no incluidas.
