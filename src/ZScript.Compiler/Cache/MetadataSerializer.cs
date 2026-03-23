using System.Text.Json;
using System.Text.Json.Nodes;
using ZScript.Compiler.Ast;
using ZScript.Compiler.Modules;
using ZScript.Compiler.Types;

namespace ZScript.Compiler.Cache;

public static class MetadataSerializer
{
    private const int FormatVersion = 1;

    public static string Serialize(string packageName, string version, string assemblyName,
        IReadOnlyDictionary<string, CompiledModule> modules)
    {
        var root = new JsonObject
        {
            ["formatVersion"] = FormatVersion,
            ["package"] = packageName,
            ["version"] = version,
            ["assemblyName"] = assemblyName,
        };

        var modulesObj = new JsonObject();
        foreach (var (name, mod) in modules)
        {
            modulesObj[name] = SerializeModule(mod);
        }
        root["modules"] = modulesObj;

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static PrecompiledPackage? Deserialize(string json, string assemblyPath)
    {
        var root = JsonNode.Parse(json) as JsonObject;
        if (root is null)
            return null;

        var formatVersion = root["formatVersion"]?.GetValue<int>() ?? 0;
        if (formatVersion != FormatVersion)
            return null;

        var packageName = root["package"]?.GetValue<string>();
        var version = root["version"]?.GetValue<string>();
        if (packageName is null || version is null)
            return null;

        var modulesNode = root["modules"] as JsonObject;
        if (modulesNode is null)
            return null;

        var modules = new Dictionary<string, PrecompiledModuleInfo>();
        foreach (var (name, moduleNode) in modulesNode)
        {
            if (moduleNode is not JsonObject moduleObj)
                continue;
            modules[name] = DeserializeModule(name, moduleObj);
        }

        return new PrecompiledPackage(packageName, version, assemblyPath, modules);
    }

    private static JsonObject SerializeModule(CompiledModule mod)
    {
        var obj = new JsonObject();

        // exportedNames
        var namesArray = new JsonArray();
        foreach (var name in mod.ExportedNames)
            namesArray.Add(name);
        obj["exportedNames"] = namesArray;

        // exportedTypes
        var typesObj = new JsonObject();
        foreach (var (name, type) in mod.ExportedTypes)
            typesObj[name] = ZTypeSerializer.Serialize(type);
        obj["exportedTypes"] = typesObj;

        // exportedClrImports
        var clrImportsObj = new JsonObject();
        foreach (var (alias, (typeName, methodName, genericArity, kind)) in mod.ExportedClrImports)
        {
            clrImportsObj[alias] = new JsonObject
            {
                ["typeName"] = typeName,
                ["methodName"] = methodName,
                ["genericArity"] = genericArity,
                ["kind"] = kind.ToString(),
            };
        }
        obj["exportedClrImports"] = clrImportsObj;

        // exportedClrNamespaces
        var nsArray = new JsonArray();
        foreach (var ns in mod.ExportedClrNamespaces)
            nsArray.Add(ns);
        obj["exportedClrNamespaces"] = nsArray;

        // exportedUnionCtors
        if (mod.ExportedUnionCtors is not null)
        {
            var unionCtorsObj = new JsonObject();
            foreach (var (caseName, unionName) in mod.ExportedUnionCtors)
                unionCtorsObj[caseName] = unionName;
            obj["exportedUnionCtors"] = unionCtorsObj;
        }

        // exportedRecordCtors
        if (mod.ExportedRecordCtors is not null)
        {
            var recordCtorsObj = new JsonObject();
            foreach (var (recordName, fieldNames) in mod.ExportedRecordCtors)
            {
                var fieldsArray = new JsonArray();
                foreach (var field in fieldNames)
                    fieldsArray.Add(field);
                recordCtorsObj[recordName] = fieldsArray;
            }
            obj["exportedRecordCtors"] = recordCtorsObj;
        }

        // hasMacros
        obj["hasMacros"] = mod.ExportedMacros.Count > 0;

        return obj;
    }

    private static PrecompiledModuleInfo DeserializeModule(string name, JsonObject obj)
    {
        // exportedNames
        var namesArray = obj["exportedNames"] as JsonArray ?? [];
        var exportedNames = new HashSet<string>();
        foreach (var n in namesArray)
        {
            if (n?.GetValue<string>() is { } s)
                exportedNames.Add(s);
        }

        // exportedTypes
        var typesObj = obj["exportedTypes"] as JsonObject;
        var exportedTypes = new Dictionary<string, ZType>();
        if (typesObj is not null)
        {
            foreach (var (tName, tNode) in typesObj)
            {
                if (tNode is not null)
                    exportedTypes[tName] = ZTypeSerializer.Deserialize(tNode);
            }
        }

        // exportedClrImports
        var clrImportsObj = obj["exportedClrImports"] as JsonObject;
        var exportedClrImports = new Dictionary<string, (string TypeName, string MethodName, int GenericArity, ClrImportKind Kind)>();
        if (clrImportsObj is not null)
        {
            foreach (var (alias, importNode) in clrImportsObj)
            {
                if (importNode is not JsonObject importObj)
                    continue;
                var typeName = importObj["typeName"]?.GetValue<string>() ?? "";
                var methodName = importObj["methodName"]?.GetValue<string>() ?? "";
                var genericArity = importObj["genericArity"]?.GetValue<int>() ?? 0;
                var kindStr = importObj["kind"]?.GetValue<string>() ?? "Static";
                Enum.TryParse<ClrImportKind>(kindStr, out var kind);
                exportedClrImports[alias] = (typeName, methodName, genericArity, kind);
            }
        }

        // exportedClrNamespaces
        var nsArray = obj["exportedClrNamespaces"] as JsonArray ?? [];
        var exportedClrNamespaces = new List<string>();
        foreach (var n in nsArray)
        {
            if (n?.GetValue<string>() is { } s)
                exportedClrNamespaces.Add(s);
        }

        // exportedUnionCtors
        Dictionary<string, string>? exportedUnionCtors = null;
        if (obj["exportedUnionCtors"] is JsonObject unionCtorsObj)
        {
            exportedUnionCtors = new Dictionary<string, string>();
            foreach (var (caseName, unionNameNode) in unionCtorsObj)
            {
                if (unionNameNode?.GetValue<string>() is { } unionName)
                    exportedUnionCtors[caseName] = unionName;
            }
        }

        // exportedRecordCtors
        Dictionary<string, List<string>>? exportedRecordCtors = null;
        if (obj["exportedRecordCtors"] is JsonObject recordCtorsObj)
        {
            exportedRecordCtors = new Dictionary<string, List<string>>();
            foreach (var (recordName, fieldsNode) in recordCtorsObj)
            {
                if (fieldsNode is not JsonArray fieldsArray)
                    continue;
                var fields = new List<string>();
                foreach (var f in fieldsArray)
                {
                    if (f?.GetValue<string>() is { } s)
                        fields.Add(s);
                }
                exportedRecordCtors[recordName] = fields;
            }
        }

        return new PrecompiledModuleInfo(
            name, exportedNames, exportedTypes, exportedClrImports,
            exportedClrNamespaces, exportedUnionCtors, exportedRecordCtors);
    }
}
