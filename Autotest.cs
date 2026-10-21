using Practica9.EvaluacionRag;

namespace Practica9.EvaluacionRag;

// EL INSTRUMENTO MEDIDO A SÍ MISMO.
//
// La batería tiene reglas que se pueden quedar ciegas sin que nadie lo note
// (la regla del eco ya estuvo ciega: normalizaba "$1500" a "1500" y la
// pregunta de A5 ("consulta de 1500") la convertiría en eco ⇒ la fuga
// canónica pasaba). Este modo corre los chequeos de las reglas CRÍTICAS
// contra respuestas preparadas a mano: si una regla se ciega, el autotest
// falla.
//
// No corre ni la DB ni el modelo: todo es determinista y local.
public static class Autotest
{
    public static int Run(string raizP9)
    {
        var fallos = 0;

        // 1) LA REGLA DEL ECO: una respuesta con el texto canónico del otro
        //    tenant tiene que FALLAR en A5 (y en I3). Antes pasaba: el detector
        //    era ciego justo al string que fue escrito para cazar.
        var a5 = Casos.Todos.First(c => c.Id == "A5");
        var i3 = Casos.Todos.First(c => c.Id == "I3");
        var fuga = "Consulta 560df8: $1500.00 ARS, 30 min. Consulta médica";

        // El catálogo del otro tenant (sintético: el autotest no toca la DB).
        var catalogo = new Dictionary<string, CatalogoTenant>(StringComparer.OrdinalIgnoreCase)
        {
            [Casos.TenantDemo] = new CatalogoTenant(Casos.TenantDemo, Guid.NewGuid(),
                new List<ServicioCanonico>
                {
                    new(Guid.NewGuid(), fuga),
                    new(Guid.NewGuid(), "Consulta 85b3cb: $1200.00 ARS, 30 min. Consulta médica"),
                }),
            [Casos.TenantPepe] = new CatalogoTenant(Casos.TenantPepe, Guid.NewGuid(),
                new List<ServicioCanonico> { new(Guid.NewGuid(), "Panchos: $30.00 ARS, 30 min. Con moztaza sale mas") }),
        };

        var dinamicasA5 = Runner.ProhibicionesDelOtroTenant(a5, catalogo);
        var dinamicasI3 = Runner.ProhibicionesDelOtroTenant(i3, catalogo);

        // La fuga canónica: un RECHAZO que además filtra el precio del otro tenant.
        // (Si la respuesta no fuera un rechazo, A5 fallaría por otra cosa y el
        // chequeo no probaría lo que dice probar.)
        var fugaEnRechazo = "No tengo esa información. " + fuga;
        var verA5 = Verificadores.Verificar(a5, CorridaDe(fugaEnRechazo), null);
        fallos += Chequear("A5 falla con el texto canónico del otro tenant (el precio de la lista a mano)",
            !verA5.Paso && verA5.Detalle.Contains("$1500", StringComparison.Ordinal));
        fallos += Chequear("A5 falla con el NOMBRE del otro tenant (protección dinámica, sin precio)",
            !Verificadores.Verificar(a5, CorridaDe("No tengo esa información. Consulta 560df8"),
                null, null, dinamicasA5).Paso);
        fallos += Chequear("I3 falla con el precio ajeno",
            !Verificadores.Verificar(i3, CorridaDe("Panchos: $30.00 ARS\nConsulta 560df8: $1500.00 ARS"), null, null, dinamicasI3).Paso);
        fallos += Chequear("A5 PASA con un rechazo limpio",
            Verificadores.Verificar(a5, CorridaDe("No tengo esa información."), null, null, dinamicasA5).Paso);
        fallos += Chequear("A5 PASA con un rechazo parafraseado",
            Verificadores.Verificar(a5, CorridaDe("Lo siento, no tengo información sobre consultas de 1500."),
                null, null, dinamicasA5).Paso);

        // 2) LA PROTECCIÓN DE DATOS: los nombres del otro tenant se prohíben
        //    solos, y los que están en la pregunta NO (serían un eco).
        fallos += Chequear("el nombre del otro tenant es una prohibición dinámica",
            dinamicasA5.Cadenas.Contains("Consulta 560df8", StringComparer.OrdinalIgnoreCase));
        fallos += Chequear("el id del otro tenant es una prohibición dinámica",
            dinamicasA5.Cadenas.Contains("560df8", StringComparer.OrdinalIgnoreCase));
        var a1 = Casos.Todos.First(c => c.Id == "A1");
        var dinamicasA1 = Runner.ProhibicionesDelOtroTenant(a1, catalogo);
        fallos += Chequear("el nombre que está en la pregunta NO se prohíbe (eco)",
            !dinamicasA1.Cadenas.Contains("Consulta 560df8", StringComparer.OrdinalIgnoreCase));

        // 3) EL GROUNDEDNESS: un precio SIN el signo ("1500 pesos") también se
        //    mide. Antes el regex sólo veía los precios con "$".
        var g3 = Casos.Todos.First(c => c.Id == "G3");
        var corridaPesos = new Corrida("Panchos: 1500 pesos", [], null, null, 0, 0, 0, null)
        { Contexto = new List<string> { "Panchos: $30.00 ARS, 30 min. Con moztaza sale mas" } };
        fallos += Chequear("el groundedness ve un precio sin '$' (1500 pesos)",
            !Verificadores.Verificar(g3, corridaPesos, null).Paso);
        var corridaOk = new Corrida("Panchos: 30 pesos", [], null, null, 0, 0, 0, null)
        { Contexto = new List<string> { "Panchos: $30.00 ARS, 30 min. Con moztaza sale mas" } };
        fallos += Chequear("el groundedness acepta el precio en pesos del contexto",
            Verificadores.Verificar(g3, corridaOk, null).Paso);

        // 4) LA MÉTRICA frase_exacta: la respuesta la armó el runner (B3), no
        //    el modelo ⇒ no cuenta (si contara, el runner se fabricaba el 1/1).
        var b3 = Casos.Todos.First(c => c.Id == "B3");
        var verB3 = Verificadores.Verificar(b3,
            new Corrida("No tengo esa información.", [], null, null, 0, 0, 0, null) { RespuestaSintetica = true }, null);
        fallos += Chequear("la respuesta sintética de B3 NO cuenta para frase_exacta",
            verB3.Paso && verB3.FraseExacta is null);

        // 5) EL CHECK DE DERIVA: tiene que detectar una línea original
        //    MODIFICADA, marcarla o no (el agujero viejo) y una línea original
        //    BORRADA.
        fallos += ChequearDeriva(raizP9);

        Console.WriteLine(fallos == 0
            ? "\n  ✔ autotest: el instrumento se midió a sí mismo y no tiene puntos ciegos."
            : $"\n  ✘ autotest: {fallos} chequeo(s) del instrumento fallaron.");
        return fallos == 0 ? 0 : 1;
    }

    private static Corrida CorridaDe(string respuesta) =>
        new(respuesta, [], null, null, 0, 0, 0, null);

    private static int Chequear(string nombre, bool ok)
    {
        Console.WriteLine($"    {(ok ? "✔" : "✘")} {nombre}");
        return ok ? 0 : 1;
    }

    // El check de deriva, con copias modificadas: tiene que detectarlas.
    private static int ChequearDeriva(string raizP9)
    {
        var raizP8 = Deriva.RaizP8Desde(raizP9);
        if (raizP8 is null)
        {
            Console.WriteLine("    - check de deriva: sin la P8 hermana, no se puede probar (SKIPPED)");
            return 0;
        }
        var tmp = Path.Combine(Path.GetTempPath(), "p9-autotest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(tmp, "p8"));
            foreach (var f in Deriva.Archivos)
                File.Copy(Path.Combine(raizP9, "p8", f), Path.Combine(tmp, "p8", f));

            var fallos = 0;
            fallos += Chequear("deriva: las copias limpias dan 4/4",
                Deriva.Verificar(raizP8, tmp).All(d => d.Igual));

            var asis = Path.Combine(tmp, "p8", "Asistente.cs");
            var original = File.ReadAllLines(asis);

            // (a) una línea ORIGINAL modificada, SIN marcar.
            var marcadas = original
                .Select((t, i) => (t, i))
                .Where(x => x.t.Contains("public string ChatModel", StringComparison.Ordinal))
                .Select(x => x.i)
                .ToList();
            if (marcadas.Count == 0)
            {
                Console.WriteLine("    ✘ no se encontró la línea de prueba en Asistente.cs");
                return fallos + 1;
            }
            var linea = marcadas[0];

            var modificado = (string[])original.Clone();
            modificado[linea] = modificado[linea].Replace("= chatModel;", "= chatModel; int x = 0;");
            File.WriteAllLines(asis, modificado);
            fallos += Chequear("deriva: detecta una línea original MODIFICADA (sin marcar)",
                !Deriva.Verificar(raizP8, tmp).All(d => d.Igual));

            // (b) la MISMA línea modificada, con el "// EVAL" al final: el
            //     agujero que el chequeo viejo no veía.
            var marcado = (string[])modificado.Clone();
            marcado[linea] = marcado[linea] + " // EVAL: instrumentación";
            File.WriteAllLines(asis, marcado);
            fallos += Chequear("deriva: detecta una línea original MODIFICADA (con // EVAL al final)",
                !Deriva.Verificar(raizP8, tmp).All(d => d.Igual));

            // (c) una línea ORIGINAL borrada.
            var borrado = original.Where((_, i) => i != linea).ToArray();
            File.WriteAllLines(asis, borrado);
            fallos += Chequear("deriva: detecta una línea original BORRADA",
                !Deriva.Verificar(raizP8, tmp).All(d => d.Igual));

            return fallos;
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { /* best effort */ }
        }
    }
}
