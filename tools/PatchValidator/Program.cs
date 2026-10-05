// Static Harmony patch validator. No game process needed.
//
// Loads the built mod DLL and the game's unhollowed Il2Cpp assemblies into a MetadataLoadContext (metadata only), then for every
// [HarmonyPatch(typeof(X), "Method"[, Type[] args])] class checks, the way Harmony will at load time:
//   * the target type and method exist (and are not ambiguous when no argument list is given);
//   * every Prefix/Postfix/Finalizer parameter is either a Harmony special (__instance, __result, __state, ___field) or matches
//     a parameter of the original BY NAME and TYPE;
//   * __instance is assignable to the target type, __result matches the return type.
//
// usage: PatchValidator <TLDOverhaul.dll> <GameDir>
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("usage: PatchValidator <mod.dll> <GameDir>"); return 2; }
        string modPath = Path.GetFullPath(args[0]);
        string game = args[1];
        string interop = Path.Combine(game, "MelonLoader", "Il2CppAssemblies");
        string mlNet6 = Path.Combine(game, "MelonLoader", "net6");

        var paths = new List<string>();
        paths.AddRange(Directory.GetFiles(Path.GetDirectoryName(typeof(object).Assembly.Location), "*.dll"));
        paths.AddRange(Directory.GetFiles(interop, "*.dll"));
        paths.AddRange(Directory.GetFiles(mlNet6, "*.dll"));
        paths.Add(modPath);
        // later duplicates (same simple name) are ignored by PathAssemblyResolver: runtime first, then interop, then MelonLoader.
        var resolver = new PathAssemblyResolver(paths);
        using var mlc = new MetadataLoadContext(resolver, typeof(object).Assembly.GetName().Name);

        Assembly mod = mlc.LoadFromAssemblyPath(modPath);
        int ok = 0, bad = 0;

        foreach (Type t in SafeTypes(mod))
        {
            var attrs = CustomAttributeData.GetCustomAttributes(t).Where(a => a.AttributeType.Name == "HarmonyPatch").ToList();
            if (attrs.Count == 0) continue;
            foreach (var a in attrs)
            {
                var ctorArgs = a.ConstructorArguments;
                Type target = ctorArgs.Count > 0 ? ctorArgs[0].Value as Type : null;
                string name = ctorArgs.Count > 1 ? ctorArgs[1].Value as string : null;
                Type[] argTypes = null;
                bool getter = false;
                if (ctorArgs.Count > 2)
                {
                    if (ctorArgs[2].Value is IEnumerable<CustomAttributeTypedArgument> arr) argTypes = arr.Select(x => (Type)x.Value).ToArray();
                    else getter = true;   // MethodType.Getter etc.
                }
                string label = t.Name + " -> " + (target != null ? target.Name : "?") + "." + name;
                if (target == null || name == null) { Console.WriteLine("SKIP  " + label + " (not a typeof+name patch)"); continue; }

                MethodBase original;
                var all = target.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                                .Where(m => m.Name == name).ToList();
                if (getter) { Console.WriteLine("SKIP  " + label + " (property accessor)"); continue; }
                if (argTypes != null)
                {
                    original = all.FirstOrDefault(m => m.GetParameters().Select(p => p.ParameterType.FullName).SequenceEqual(argTypes.Select(x => x.FullName)));
                    if (original == null) { Console.WriteLine("FAIL  " + label + ": no overload with (" + string.Join(", ", argTypes.Select(x => x.Name)) + ")"); bad++; continue; }
                }
                else
                {
                    if (all.Count == 0) { Console.WriteLine("FAIL  " + label + ": method not found"); bad++; continue; }
                    if (all.Count > 1) { Console.WriteLine("FAIL  " + label + ": ambiguous (" + all.Count + " overloads) - add an argument list"); bad++; continue; }
                    original = all[0];
                }

                var problems = new List<string>();
                var origParams = original.GetParameters();
                Type returnType = (original as MethodInfo)?.ReturnType;

                foreach (var pm in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (pm.Name != "Prefix" && pm.Name != "Postfix" && pm.Name != "Finalizer") continue;
                    if (pm.Name == "Prefix" && pm.ReturnType.Name != "Void" && pm.ReturnType.Name != "Boolean") problems.Add("Prefix returns " + pm.ReturnType.Name + " (must be void or bool)");
                    foreach (var p in pm.GetParameters())
                    {
                        string n = p.Name;
                        Type pt = p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType;
                        if (n == "__instance")
                        {
                            if (original.IsStatic) problems.Add(pm.Name + ": __instance on a static method");
                            else if (!pt.IsAssignableFrom(original.DeclaringType) && !original.DeclaringType.IsAssignableFrom(pt)) problems.Add(pm.Name + ": __instance type " + pt.Name + " vs " + original.DeclaringType.Name);
                        }
                        else if (n == "__result")
                        {
                            if (returnType == null || returnType.Name == "Void") problems.Add(pm.Name + ": __result but the method returns void");
                            else if (pt.FullName != returnType.FullName) problems.Add(pm.Name + ": __result is " + pt.Name + " but the method returns " + returnType.Name);
                        }
                        else if (n == "__state" || n == "__originalMethod" || n == "__args" || n == "__runOriginal") { /* free */ }
                        else if (n.StartsWith("___"))
                        {
                            string field = n.Substring(3);
                            if (original.DeclaringType.GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static) == null) problems.Add(pm.Name + ": no field '" + field + "'");
                        }
                        else if (n.StartsWith("__") && int.TryParse(n.Substring(2), out int idx))
                        {
                            if (idx >= origParams.Length) problems.Add(pm.Name + ": positional " + n + " out of range");
                        }
                        else
                        {
                            var op = origParams.FirstOrDefault(x => x.Name == n);
                            if (op == null) problems.Add(pm.Name + ": parameter '" + n + "' does not exist on the original (" + string.Join(", ", origParams.Select(x => x.Name)) + ")");
                            else
                            {
                                Type ot = op.ParameterType.IsByRef ? op.ParameterType.GetElementType() : op.ParameterType;
                                if (ot.FullName != pt.FullName) problems.Add(pm.Name + ": '" + n + "' is " + pt.Name + " but the original takes " + ot.Name);
                                else if (p.ParameterType.IsByRef && !op.ParameterType.IsByRef && pm.Name != "Prefix") { /* ref on a by-value arg in a postfix is legal but modifies nothing; fine */ }
                            }
                        }
                    }
                }
                if (problems.Count == 0) { ok++; Console.WriteLine("ok    " + label); }
                else { bad++; Console.WriteLine("FAIL  " + label); foreach (var pr in problems) Console.WriteLine("        - " + pr); }
            }
        }
        Console.WriteLine();
        Console.WriteLine("patches ok: " + ok + "   problems: " + bad);
        return bad == 0 ? 0 : 1;
    }

    private static IEnumerable<Type> SafeTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(x => x != null); }
    }
}
