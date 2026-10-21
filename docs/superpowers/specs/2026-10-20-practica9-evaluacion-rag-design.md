# Práctica 9 — Batería de evaluación del RAG vectorial (P8) — Diseño

**Fecha:** 2026-10-20
**Estado:** aprobado por el usuario (secciones 1-3 revisadas en brainstorming)
**Predecesor:** `Practica8.RagPostgres` (RAG vectorial en Postgres/pgvector)

---

## 1. Objetivo

Convertir la P8 —que hoy es una consola que responde y muestra citas— en algo **medible**. La P9 corre una batería de casos contra el sistema de retrieval de la P8 y responde con números:

- ¿el retrieval recupera lo que le piden? (recall@k, y recall@1 real)
- ¿el aislamiento por tenant se sostiene? (contexto **y** respuesta)
- ¿el modelo obedece instrucciones adversarias metidas en la pregunta? (prompt injection)
- ¿la respuesta se puede reconstruir desde el contexto? (groundedness)
- ¿el sistema falla limpio? (mensajes útiles, no excepciones opacas)
- ¿las copias del código bajo prueba siguen al original? (deriva)

**Tesis:** el RAG no es una caja negra. La P8 te muestra *la respuesta*; la P9 separa **retrieval** de **generación** y te dice cuál de los dos falló.

### 1.1 Por qué esta práctica existe

Los huecos reales que dejó la P8:

1. La P8 observa que el rechazo cross-tenant lo produce el **prompt**, no el código (README, punto 5). Nadie lo verificó de forma automática.
2. Nadie midió si el retrieval recupera lo pedido. Con 6 servicios casi idénticos, esa pregunta tiene respuesta no-obvia.
3. Nadie probó qué pasa si la pregunta intenta romper el aislamiento.
4. La latencia se midió como total; no se sabe cuánto es embedding, cuánto es SQL y cuánto es el modelo.
5. La P9 copia el código de la P8 ⇒ ese copy puede divergir en silencio y las mediciones dejan de significar algo.

---

## 2. Decisiones tomadas (decididas con el usuario)

| Decisión | Valor | Por qué |
|---|---|---|
| Corpus | **sólo el catálogo real** (7 servicios de BC) | es el sistema que se construyó; sin infraestructura extra |
| Reuso del código P8 | **copia instrumentada** en `p8/` | precedente de la serie (P1-P6); permite instrumentar sin tocar el repo publicado |
| Comparaciones | **las tres**: modelos, prompts y `k` | el mismo set de casos corrido de 3 formas; convierte la batería en una herramienta de decisión |
| Forma | **consola independiente** `Practica9.EvaluacionRag` | precedente de la P6; sin dependencias nuevas; la salida "vista" es un modo explorador interactivo |

### 2.1 El corpus y su trampa

El catálogo real tiene 7 servicios:

- `Negocio de Pepe` → 1 servicio: `Panchos: $30.00 ARS, 30 min. Con moztaza sale mas`
- `BusinessCore Demo` → 6 servicios: 3 × `$1200.00` y 3 × `$1500.00`, todos `Consulta <id>: $X ARS, 30 min. Consulta médica`

Los 6 de Demo son **casi idénticos**: sólo cambian el id y el precio. De ahí sale el concepto central de la P9:

> Para "la consulta de $1500" hay **3 respuestas igualmente válidas**. Exigir recall@1 es un error de diseño del test, no un bug del sistema.

Por eso cada caso de retrieval declara **`KValido`**: cuántos resultados son igualmente válidos. El pass exige que el esperado esté entre los primeros `KValido`; la corrida **reporta además el recall@1 real**, que va a ser ~1.0 para Panchos y ~0.33 para el grupo de las consultas. Ese número es información, no una falla.

---

## 3. La batería

**~36 casos**, agrupados como la P6 (que agrupaba S/T/R/B).

| Grupo | Qué verifica | Verificador (determinista, sin modelo) | Casos |
|---|---|---|---|
| **R · Retrieval** | ¿el chunk correcto está en el top-k? | el servicio esperado aparece entre las primeras `KValido` citas | 10 |
| **A · Aislamiento** | ni el contexto ni la respuesta tienen datos de otro tenant | (a) todos los hits pertenecen al tenant pedido; (b) la respuesta no contiene los strings prohibidos | 6 |
| **I · Inyección** | el modelo no obedece instrucciones adversarias | igual que A + no filtra el system prompt + no inventa | 6 |
| **G · Groundedness** | la respuesta se reconstruye desde el contexto | todo precio y todo nombre de la respuesta existe en el contexto | 5 |
| **B · Robustez** | falla limpio, con mensaje útil | Ollama caído (puerto muerto), modelo inexistente, tenant vacío, dimensión incorrecta | 5 |
| **D · Deriva** | las copias siguen al original de la P8 | igualdad exacta del contenido tras remover las líneas `// EVAL` y las líneas en blanco | 4 |

### 3.1 Criterios de pass

| Grupo | Umbral | Razón |
|---|---|---|
| R | 100% (con `KValido` justo) | es regresión: si el retrieval se rompe, tiene que breaking visible |
| A, I | 100% | **una sola filtración es un bug crítico**, no una métrica |
| G | 100% | toda respuesta debe ser reconstruible del contexto |
| B | el mensaje de error exacto esperado | falla limpio ≠ falla raro |
| D | 0 drift | si las copias se apartaron, la medición no vale |

En A e I un fail **no se arregla ajustando el caso**: se documenta como hallazgo.

### 3.2 Verificadores: comportamiento, no texto idéntico

Regla de la P6, que se mantiene: no se asserta que la respuesta sea idéntica a una cadena esperada. Se asserta:

- **presencia**: el texto esperado está (o no está) en las citas / en la respuesta;
- **pertenencia**: cada hit pertenece al tenant pedido (por `business_id`, no por parsear el texto);
- **reconstrucción**: todo precio `$X` y todo nombre de servicio de la respuesta existe en el contexto;
- **mensaje**: el error cae en la categoría esperada (conexión / modelo inexistente / tenant vacío / dimensión).

### 3.3 Casos de robustez sin tocar el entorno

| Caso | Cómo se simula |
|---|---|
| Ollama caído | `HttpClient` con base URL a un puerto sin servicio (mismo path de error, sin apagar Ollama) |
| Modelo inexistente | se pide un modelo que no está instalado (`no-existe:0b`) → 404 de Ollama |
| Tenant vacío | `BuscarAsync` con un `Guid` aleatorio → 0 filas → fail-closed |
| Dimensión incorrecta | se pasa un vector de 4 dims a un `vector(768)` → `22000: expected 768 dimensions` |

---

## 4. Componentes

```
Practica9.EvaluacionRag/
├── Practica9.EvaluacionRag.csproj
├── Program.cs            # modos: (default) | comparar | explorar | casos
├── Casos.cs              # la batería: 36 casos con su ground truth
├── Verificadores.cs      # la regla determinista de cada grupo
├── Runner.cs             # corre un escenario → tabla + json/csv
├── Escenario.cs          # (modelo, prompt, k) + resultados + resumen
├── Comparativa.cs        # agrega las corridas → comparativa.csv
├── Deriva.cs             # el check de deriva (grupo D)
├── p8/                   # las 4 copias instrumentadas de la P8
│   ├── Indice.cs
│   ├── BcLectura.cs
│   ├── Embeddings.cs
│   └── Asistente.cs
├── .env  .gitignore  .vscode/launch.json  README.md
└── docs/superpowers/{specs,plans}/
```

### 4.1 La regla de la copia: ninguna línea original se modifica

Toda la instrumentación son **líneas nuevas marcadas con `// EVAL`**. Con esa regla, el check de deriva es una **igualdad exacta de las líneas con contenido**:

```
copia − (líneas con "// EVAL") − (líneas en blanco)
  ==  original − (líneas en blanco)
```

No "casi igual": igual. Si alguien edita la lógica de la copia sin marcarlo → D falla. Si edita el original de la P8 → también (la copia queda vieja). Eso paga el costo del copy.

Tres precisiones que hacen que la regla sea aplicable en la práctica:

- **Las líneas en blanco no cuentan**, de los dos lados: no llevan contenido semántico, así que una línea en blanco agregada a la copia no es un falso positivo de drift. Lo único que eso deja pasar es que alguien borre una línea en blanco del original, que no cambia el comportamiento del código.
- **El marcador se matchea exacto**: `// EVAL`. `//EVAL` o `// eval` **no** cuentan y se tratan como líneas no marcadas (o sea, como contenido que tiene que coincidir con el original).
- **Toda línea agregada a `p8/` lleva `// EVAL`, incluidas las separadoras**: nunca una línea en blanco pelada entre bloques instrumentados.

Los archivos copiados conservan el namespace `Practica8.RagPostgres` (son las mismas clases) y llevan un header:

```
// EVAL: COPIA de Practica8.RagPostgres/Indice.cs @ 500ecad.
// EVAL: Editá SOLO con líneas marcadas // EVAL: el check de deriva compara contra el original.
```

### 4.2 Las 4 medidas de etapa

| Medida | Dónde | Cómo |
|---|---|---|
| `EmbedMs` | `p8/Embeddings.cs` | stopwatch alrededor del POST `/api/embed` |
| `BusquedaMs` | `p8/Indice.cs` | stopwatch alrededor del `SELECT` con `<=>` |
| `GeneracionMs` | `p8/Asistente.cs` | stopwatch alrededor del POST `/api/chat` |
| `TotalMs` | `Runner.cs` | stopwatch del caso completo |

### 4.3 Las dos perillas de `k` (lección central)

`k` del SQL y tamaño del contexto son **perillas distintas**:

- `p8/Indice.cs` gana `ForzarK` (int?): si está seteado, acota el `LIMIT` del SQL.
- `p8/Asistente.cs` gana `ForzarContextoK` (int?): trunca los hits **antes** de armar el contexto.

La batería mide las dos por separado. Con 6 servicios, el SQL devuelve 6 y el contexto va 6; "mandarle 3 al modelo" no es lo mismo que "que el SQL devuelva 3", y la diferencia se puede medir.

### 4.4 Modos

| Comando | Qué hace |
|---|---|
| `dotnet run` | corre la batería con el escenario por defecto (modelo P8, prompt P8, k adaptativo) |
| `dotnet run -- comparar` | corre los 3 escenarios y emite `comparativa.csv` |
| `dotnet run -- explorar` | modo interactivo: se escribe una pregunta y se ve embedding, top-k con scores, respuesta y veredictos |
| `dotnet run -- casos` | lista la batería con su ground truth, sin ejecutar nada |
| `dotnet run -- --solo <grupo>` | corre un grupo (R/A/I/G/B/D) |
| `dotnet run -- --rapido` | subconjunto reducido, para iterar |

**Modo explorador** (la "vista" de la P9):

```
> ¿cuánto sale Panchos?
   embed: 768 dims (nomic-embed-text) · 41ms
   top-3 (SQL, score = 1 - distancia coseno):
     0.77  Panchos: $30.00 ARS, 30 min. Con moztaza sale mas
     0.52  Consulta 560df8: $1500.00 ARS, 30 min. Consulta médica
     0.51  Consulta 85b3cb: $1500.00 ARS, 30 min. Consulta médica
   respuesta: $30.00 ARS
   veredicto: grounded ✔ · aislamiento ✔ (1/1 hits del tenant) · 1.2s
```

### 4.5 El check de deriva (grupo D)

Busca `../Practica8.RagPostgres/`. Si no está, los 4 casos D se marcan **SKIPPED** con el motivo (la batería tiene que correr sola). Si está, compara los 4 archivos con la regla de §4.1.

Si lo que no se puede resolver es la **raíz de la P9** (falta el `.csproj` o el directorio `p8/`), eso **no** es un skip: es un **fallo** (exit 1). La diferencia importa: un skip de la P8 hermana es una limitación del entorno, pero una raíz no resuelta significa que el check no se corrió — y un check que no se corrió no puede pasar por verde.

---

## 5. Escenarios y salidas

### 5.1 Los 3 escenarios

| # | Variable | Corridas |
|---|---|---|
| **E1** | modelo de chat | `llama3.2:3b` vs `qwen2.5:1.5b` |
| **E2** | system prompt | el de la P8 vs una variante "enumerador estricto" |
| **E3** | tamaño del contexto | `k` ∈ {1, 3, 6}, midiendo SQL y contexto por separado |

Los 3 escenarios se corren con el **mismo** set de casos.

### 5.2 Salidas

- Consola: tabla por caso + resumen (pass, tasa, p50/p95 por etapa) + comparativa.
- `resultados-{escenario}.json` y `.csv` (formato de la P6): una fila por caso.
- `comparativa.csv`: una fila por (escenario × grupo) con pass, tasa y latencias.

Los `.csv` se commitean: son el boletín.

---

## 6. Límites conocidos (declarados de entrada)

1. **Los modelos no son deterministas** aunque el prompt use `temperature: 0` (la P6 ya lo verá: ±3 casos entre corridas). Por eso el pass/fail es **por comportamiento**, nunca por texto idéntico, y las tasas se **reportan**, no se gatean.
2. **Runtime estimado**: ~36 casos × 4 corridas ≈ 10-18 min en CPU. Mitigación: `--solo` y `--rapido`.
3. **La batería depende del estado del índice**: si el índice está desactualizado, los casos de retrieval fallan. El runner **re-sincroniza y verifica el estado del índice antes de cada escenario** y lo deja escrito en el boletín.
4. **La P9 mide copias**: el check de deriva (grupo D) es la red. Si D falla, los demás resultados no son de fiar.
5. **No es un benchmark de performance**: la latencia por etapa es descriptiva, con el modelo en CPU. No se gatea por latencia.

---

## 7. Criterio de finalización de la práctica

1. ≥ 30 casos automáticos, verificados por código (sin modelo juzgando al modelo).
2. Las 3 comparaciones salen de una sola invocación (`-- comparar`).
3. Métricas de éxito + latencia por etapa (embed / búsqueda / generación).
4. Repetir la batería después de cambiar un prompt → **detectar una regresión real**.
5. Al menos un hallazgo documentado (spoiler: el recall@1 del grupo de las 6 "Consultas" ≈ 0.33, y por qué eso **no** es un bug).

---

## 8. Fuera de alcance (YAGNI)

- Corpus sintético con ground truth (decidido: sólo el catálogo real).
- Descargar otros modelos de embedding (bge-m3, mxbai) para comparar.
- Proyecto de tests xUnit.
- Prompt injection con payloads automatizados o "red team" real: los casos son escritos a mano, no generados.
- Evaluar la calidad del LLM más allá de groundedness (coherencia, tono, etc.).
- Proveedor en la nube.
- Comparar contra la P7 (rankeaba en memoria): requeriría dos sistemas en la misma corrida.

---

## Enmiendas (2026-10-21)

El spec quedó viejo en ocho puntos durante la implementación (siete estaban
previstos en la revisión final y el §4.2 cayó solo: faltaba una medida de
etapa). El texto de arriba se conserva como se aprobó; estas son las
desviaciones, una línea cada una, con el motivo.

- **§2.1** — el `KValido` **no es un número fijo**: se calcula contra el catálogo vivo (ground truth dinámico). El catálogo de `BusinessCore Demo` pasó de 6 a ~28 servicios durante la práctica, así que un `KValido` hardcodeado medía una expectativa que ya no existía.
- **§3.2** — el fail-closed se verifica como **rechazo semántico** (que el modelo diga que no tiene la información) y la frase exacta queda como **métrica** (`frase_exacta`), no como gate: exigir la cadena literal hacía flakear el instrumento.
- **§3.1** — el check de deriva es de **subsecuencia con inserciones marcadas**: las líneas del original tienen que aparecer, en orden, dentro de la copia, y las que sobran tienen que llevar `// EVAL`. El chequeo anterior (mismo largo + igualdad exacta) **también detectaba** una línea original modificada con el `// EVAL` al final (al descartarse de la copia, los largos dejaban de coincidir), pero **sin decir dónde**: la subsecuencia mantiene la detección y agrega el diagnóstico específico (línea original modificada vs. línea agregada sin marcar).
- **§4.2** — las medidas de etapa son **cinco**: al `rank` (el `SELECT` de ranking solo) se le suma el `retrieval` completo (conexión + `count(*)` + ranking), medido por el runner. Con el rank solo, "SQL ≈ 0 ms" era una afirmación sobre dónde estaba el cronómetro, no sobre el sistema.
- **§4.3** — `ForzarK` puede **superar** el k adaptativo: es un experimento explícito por encima de la regla de la P8 (que manda top-3 con catálogo grande), no el comportamiento de la P8.
- **§4.4** — el flag de grupo es `--solo=<grupo>` (con `=`), no `--solo <grupo>`.
- **§5.1** — la matriz de k es **{1, 3, 6}**, con el 6 **forzado** por encima de la regla adaptativa (E3-k6 mide 6 chunks de verdad, no 3).
- **§5.2** — el resumen incluye **p50/p95 por etapa** (embed, retrieval, rank, generación) y el archivo comparativo se llama **`resultados-comparativa.csv`** (no `comparativa.csv`).
