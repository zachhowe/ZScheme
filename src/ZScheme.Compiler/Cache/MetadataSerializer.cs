using System.Text.Json;
using System.Text.Json.Nodes;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Modules;
using ZScheme.Compiler.Syntax;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Cache;

public static class MetadataSerializer
{
    private const int FormatVersion = 1;

    public static string Serialize(string packageName, string version, string assemblyName,
        IReadOnlyDictionary<string, CompiledModule> modules,
        string? importPrefix = null, string? defaultModule = null)
    {
        var root = new JsonObject
        {
            ["formatVersion"] = FormatVersion,
            ["package"] = packageName,
            ["version"] = version,
            ["assemblyName"] = assemblyName
        };

        if (importPrefix is not null)
            root["importPrefix"] = importPrefix;
        if (defaultModule is not null)
            root["defaultModule"] = defaultModule;

        var modulesObj = new JsonObject();
        foreach (var (name, mod) in modules) modulesObj[name] = SerializeModule(mod);
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

        var importPrefix = root["importPrefix"]?.GetValue<string>();
        var defaultModule = root["defaultModule"]?.GetValue<string>();

        var modules = new Dictionary<string, CompiledModule>();
        foreach (var (name, moduleNode) in modulesNode)
        {
            if (moduleNode is not JsonObject moduleObj)
                continue;
            modules[name] = DeserializeModule(name, moduleObj, assemblyPath);
        }

        return new PrecompiledPackage(packageName, version, assemblyPath, modules, importPrefix, defaultModule);
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
        foreach (var (alias, (typeName, methodName, genericArity, kind, constraints)) in mod.ExportedClrImports)
        {
            var importObj = new JsonObject
            {
                ["typeName"] = typeName,
                ["methodName"] = methodName,
                ["genericArity"] = genericArity,
                ["kind"] = kind.ToString()
            };
            if (constraints is { Count: > 0 })
            {
                var constraintsObj = new JsonObject();
                foreach (var (param, constraintKind) in constraints)
                    constraintsObj[param] = constraintKind.ToString();
                importObj["constraints"] = constraintsObj;
            }

            clrImportsObj[alias] = importObj;
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

        // exportedClassInterfaces
        if (mod.ExportedClassInterfaces is not null)
        {
            var classInterfacesObj = new JsonObject();
            foreach (var (className, interfaces) in mod.ExportedClassInterfaces)
            {
                var interfacesArray = new JsonArray();
                foreach (var iface in interfaces)
                    interfacesArray.Add(iface);
                classInterfacesObj[className] = interfacesArray;
            }

            obj["exportedClassInterfaces"] = classInterfacesObj;
        }

        // exportedMacros
        if (mod.ExportedMacros.Count > 0)
        {
            var macrosObj = new JsonObject();
            foreach (var (name, macroDef) in mod.ExportedMacros)
                macrosObj[name] = MacroSerializer.Serialize(macroDef);
            obj["exportedMacros"] = macrosObj;
        }

        // typeDeclarations — serialize UnionDecl/RecordDecl from ExportedIrDefinitions
        var typeDecls = mod.ExportedIrDefinitions
            .Where(d => d is IrNode.UnionDecl or IrNode.RecordDecl)
            .ToList();
        if (typeDecls.Count > 0)
        {
            var typeDeclsArray = new JsonArray();
            foreach (var decl in typeDecls)
                if (decl is IrNode.UnionDecl union)
                    typeDeclsArray.Add(SerializeUnionDecl(union));
                else if (decl is IrNode.RecordDecl record)
                    typeDeclsArray.Add(SerializeRecordDecl(record));
            obj["typeDeclarations"] = typeDeclsArray;
        }

        return obj;
    }

    private static JsonObject SerializeUnionDecl(IrNode.UnionDecl union)
    {
        var typeParamsArray = new JsonArray();
        foreach (var tp in union.TypeParams)
            typeParamsArray.Add(tp);

        var casesArray = new JsonArray();
        foreach (var c in union.Cases)
        {
            var fieldsArray = new JsonArray();
            foreach (var f in c.Fields)
                fieldsArray.Add(new JsonObject
                {
                    ["name"] = f.Name,
                    ["type"] = ZTypeSerializer.Serialize(f.Type)
                });
            casesArray.Add(new JsonObject
            {
                ["name"] = c.Name,
                ["fields"] = fieldsArray
            });
        }

        return new JsonObject
        {
            ["kind"] = "union",
            ["name"] = union.Name,
            ["typeParams"] = typeParamsArray,
            ["cases"] = casesArray
        };
    }

    private static JsonObject SerializeRecordDecl(IrNode.RecordDecl record)
    {
        var typeParamsArray = new JsonArray();
        foreach (var tp in record.TypeParams)
            typeParamsArray.Add(tp);

        var fieldsArray = new JsonArray();
        foreach (var f in record.Fields)
            fieldsArray.Add(new JsonObject
            {
                ["name"] = f.Name,
                ["type"] = ZTypeSerializer.Serialize(f.Type)
            });

        return new JsonObject
        {
            ["kind"] = "record",
            ["name"] = record.Name,
            ["typeParams"] = typeParamsArray,
            ["fields"] = fieldsArray
        };
    }

    private static CompiledModule DeserializeModule(string name, JsonObject obj, string assemblyPath)
    {
        // exportedNames
        var namesArray = obj["exportedNames"] as JsonArray ?? [];
        var exportedNames = new HashSet<string>();
        foreach (var n in namesArray)
            if (n?.GetValue<string>() is { } s)
                exportedNames.Add(s);

        // exportedTypes
        var typesObj = obj["exportedTypes"] as JsonObject;
        var exportedTypes = new Dictionary<string, ZType>();
        if (typesObj is not null)
            foreach (var (tName, tNode) in typesObj)
                if (tNode is not null)
                    exportedTypes[tName] = ZTypeSerializer.Deserialize(tNode);

        // exportedClrImports
        var clrImportsObj = obj["exportedClrImports"] as JsonObject;
        var exportedClrImports =
            new Dictionary<string, (string TypeName, string MethodName, int GenericArity, ClrImportKind Kind,
                IReadOnlyDictionary<string, GenericConstraintKind>? Constraints)>();
        if (clrImportsObj is not null)
            foreach (var (alias, importNode) in clrImportsObj)
            {
                if (importNode is not JsonObject importObj)
                    continue;
                var typeName = importObj["typeName"]?.GetValue<string>() ?? "";
                var methodName = importObj["methodName"]?.GetValue<string>() ?? "";
                var genericArity = importObj["genericArity"]?.GetValue<int>() ?? 0;
                var kindStr = importObj["kind"]?.GetValue<string>() ?? "Static";
                Enum.TryParse<ClrImportKind>(kindStr, out var kind);
                Dictionary<string, GenericConstraintKind>? constraints = null;
                if (importObj["constraints"] is JsonObject constraintsObj)
                {
                    constraints = new Dictionary<string, GenericConstraintKind>();
                    foreach (var (param, constraintNode) in constraintsObj)
                    {
                        var constraintStr = constraintNode?.GetValue<string>() ?? "";
                        if (Enum.TryParse<GenericConstraintKind>(constraintStr, out var constraintKind))
                            constraints[param] = constraintKind;
                    }
                }

                exportedClrImports[alias] = (typeName, methodName, genericArity, kind, constraints);
            }

        // exportedClrNamespaces
        var nsArray = obj["exportedClrNamespaces"] as JsonArray ?? [];
        var exportedClrNamespaces = new List<string>();
        foreach (var n in nsArray)
            if (n?.GetValue<string>() is { } s)
                exportedClrNamespaces.Add(s);

        // exportedUnionCtors
        Dictionary<string, string>? exportedUnionCtors = null;
        if (obj["exportedUnionCtors"] is JsonObject unionCtorsObj)
        {
            exportedUnionCtors = new Dictionary<string, string>();
            foreach (var (caseName, unionNameNode) in unionCtorsObj)
                if (unionNameNode?.GetValue<string>() is { } unionName)
                    exportedUnionCtors[caseName] = unionName;
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
                    if (f?.GetValue<string>() is { } s)
                        fields.Add(s);
                exportedRecordCtors[recordName] = fields;
            }
        }

        // exportedClassInterfaces
        Dictionary<string, IReadOnlyList<string>>? exportedClassInterfaces = null;
        if (obj["exportedClassInterfaces"] is JsonObject classInterfacesObj)
        {
            exportedClassInterfaces = new Dictionary<string, IReadOnlyList<string>>();
            foreach (var (className, interfacesNode) in classInterfacesObj)
            {
                if (interfacesNode is not JsonArray interfacesArray)
                    continue;
                var interfaces = new List<string>();
                foreach (var i in interfacesArray)
                    if (i?.GetValue<string>() is { } s)
                        interfaces.Add(s);
                exportedClassInterfaces[className] = interfaces;
            }
        }

        // exportedMacros
        Dictionary<string, MacroDefinition>? exportedMacros = null;
        if (obj["exportedMacros"] is JsonObject macrosObj)
        {
            exportedMacros = new Dictionary<string, MacroDefinition>();
            foreach (var (macroName, macroNode) in macrosObj)
                if (macroNode is not null)
                    exportedMacros[macroName] = MacroSerializer.Deserialize(macroNode);
        }

        // typeDeclarations
        List<IrNode>? typeDeclarations = null;
        if (obj["typeDeclarations"] is JsonArray typeDeclsArray)
        {
            typeDeclarations = [];
            foreach (var declNode in typeDeclsArray)
            {
                if (declNode is not JsonObject declObj)
                    continue;
                var kind = declObj["kind"]?.GetValue<string>();
                if (kind == "union")
                    typeDeclarations.Add(DeserializeUnionDecl(declObj));
                else if (kind == "record")
                    typeDeclarations.Add(DeserializeRecordDecl(declObj));
            }
        }

        return new CompiledModule(
            name,
            assemblyPath,
            exportedNames,
            exportedTypes,
            exportedClrImports,
            typeDeclarations ?? [],
            exportedClrNamespaces,
            exportedMacros ?? new Dictionary<string, MacroDefinition>(),
            exportedUnionCtors,
            exportedRecordCtors,
            ExportedClassInterfaces: exportedClassInterfaces,
            PrecompiledAssemblyPath: assemblyPath);
    }

    private static IrNode.UnionDecl DeserializeUnionDecl(JsonObject obj)
    {
        var name = obj["name"]?.GetValue<string>() ?? "";
        var typeParams = new List<string>();
        if (obj["typeParams"] is JsonArray tpArray)
            foreach (var tp in tpArray)
                if (tp?.GetValue<string>() is { } s)
                    typeParams.Add(s);

        var cases = new List<IrUnionCase>();
        if (obj["cases"] is JsonArray casesArray)
            foreach (var caseNode in casesArray)
            {
                if (caseNode is not JsonObject caseObj)
                    continue;
                var caseName = caseObj["name"]?.GetValue<string>() ?? "";
                var fields = DeserializeFields(caseObj["fields"] as JsonArray);
                cases.Add(new IrUnionCase(caseName, fields));
            }

        return new IrNode.UnionDecl(name, typeParams, cases);
    }

    private static IrNode.RecordDecl DeserializeRecordDecl(JsonObject obj)
    {
        var name = obj["name"]?.GetValue<string>() ?? "";
        var typeParams = new List<string>();
        if (obj["typeParams"] is JsonArray tpArray)
            foreach (var tp in tpArray)
                if (tp?.GetValue<string>() is { } s)
                    typeParams.Add(s);

        var fields = DeserializeFields(obj["fields"] as JsonArray);
        return new IrNode.RecordDecl(name, typeParams, fields);
    }

    private static List<IrField> DeserializeFields(JsonArray? fieldsArray)
    {
        var fields = new List<IrField>();
        if (fieldsArray is null)
            return fields;

        foreach (var fieldNode in fieldsArray)
        {
            if (fieldNode is not JsonObject fieldObj)
                continue;
            var fieldName = fieldObj["name"]?.GetValue<string>() ?? "";
            var typeNode = fieldObj["type"];
            var fieldType = typeNode is not null
                ? ZTypeSerializer.Deserialize(typeNode)
                : ZType.Unit;
            fields.Add(new IrField(fieldName, fieldType));
        }

        return fields;
    }
}
