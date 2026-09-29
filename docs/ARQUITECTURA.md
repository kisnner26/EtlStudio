# Arquitectura

```mermaid
flowchart LR
    U[Enunciado / adjuntos] --> P[Plan con IA<br/>revision humana obligatoria]
    P --> V{DwSpec.Validate}
    V -->|invalido| X[Se detiene]
    V --> D1[dsn: conecta al origen]
    D1 --> D2[sql: conecta a SQL Server]
    D2 --> D3[profile: perfila el origen<br/>nulos, vacio, fechas]
    D3 --> D4[dw: base, tablas, stg]
    D4 --> D5[pkg: genera .dtsx]
    D5 --> D6[validate: dtexec /Validate]
    D6 --> D7[load: dtexec]
    D7 --> D8[verify + reconciliacion]
    D8 --> D9[report .docx]
```

## Dentro del paquete SSIS

```mermaid
flowchart LR
    A[SQL_Auditoria_Inicio] --> B[SQL_Preparar_Staging]
    B --> C[DFT_Poblar_Dimensiones<br/>-> stg.Dim_*]
    C --> E[DFT_Extraer_Origen<br/>Lookups contra stg.Dim_*<br/>fallan si no hay dimension<br/>-> stg.Fact_*]
    E --> F[DFT_Cargar_Metas_Excel<br/>opcional -> stg.Metas]
    F --> G[SQL_Publicar<br/>una transaccion stg -> dbo]
    G --> H[SQL_Auditoria_Fin]
```

- **`src/Core`**: modelo (`Spec`), generadores (`DdlGenerator`, `SsisBuilder`, `Verification`), orquestacion (`Pipeline`), calidad (`Reconciliation`, `PackageInspector`), operacion (`AppLog`, `Notifier`, `Requirements`).
- **`src/UI`**: interfaz WPF; **`src/Program.cs`**: comandos de consola.
- Lo que no depende de SSIS ni de WPF se prueba en cualquier sistema con `tests/run.sh` (Mono).

## Politica de revision de la IA

El plan que propone la IA se valida por **formato** (`DwSpec.Validate`), no por significado. Antes de ejecutar, una persona debe revisar: la consulta de origen, las llaves de negocio, las medidas y columnas calculadas, y las dimensiones. El reporte `.docx` deja constancia de lo ejecutado.
