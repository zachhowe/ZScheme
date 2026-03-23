using System.Text.Json.Nodes;
using ZScript.Compiler.Types;

namespace ZScript.Compiler.Cache;

public static class ZTypeSerializer
{
    public static JsonNode Serialize(ZType type)
    {
        return type switch
        {
            ZType.ZPrimitiveType prim => new JsonObject
            {
                ["kind"] = "primitive",
                ["primitiveKind"] = prim.Kind.ToString(),
            },
            ZType.ZTypeVar tv => new JsonObject
            {
                ["kind"] = "typeVar",
                ["id"] = tv.Id,
            },
            ZType.ZFuncType fn => SerializeFuncType(fn),
            ZType.ZNamedType named => SerializeNamedType(named),
            ZType.ZForAllType forall => SerializeForAllType(forall),
            ZType.ZConstrainedVar cv => SerializeConstrainedVar(cv),
            _ => throw new ArgumentException($"Unknown ZType variant: {type.GetType().Name}"),
        };
    }

    public static ZType Deserialize(JsonNode node)
    {
        var obj = node as JsonObject
            ?? throw new ArgumentException("Expected a JSON object for ZType");

        var kind = obj["kind"]?.GetValue<string>()
            ?? throw new ArgumentException("Missing 'kind' field in ZType JSON");

        return kind switch
        {
            "primitive" => DeserializePrimitive(obj),
            "typeVar" => DeserializeTypeVar(obj),
            "fn" => DeserializeFuncType(obj),
            "named" => DeserializeNamedType(obj),
            "forall" => DeserializeForAllType(obj),
            "constrained" => DeserializeConstrainedVar(obj),
            _ => throw new ArgumentException($"Unknown ZType kind: {kind}"),
        };
    }

    private static JsonObject SerializeFuncType(ZType.ZFuncType fn)
    {
        var paramsArray = new JsonArray();
        foreach (var p in fn.Params)
        {
            paramsArray.Add(Serialize(p));
        }

        return new JsonObject
        {
            ["kind"] = "fn",
            ["params"] = paramsArray,
            ["return"] = Serialize(fn.Return),
        };
    }

    private static JsonObject SerializeNamedType(ZType.ZNamedType named)
    {
        var argsArray = new JsonArray();
        foreach (var arg in named.TypeArgs)
        {
            argsArray.Add(Serialize(arg));
        }

        return new JsonObject
        {
            ["kind"] = "named",
            ["name"] = named.Name,
            ["typeArgs"] = argsArray,
        };
    }

    private static JsonObject SerializeForAllType(ZType.ZForAllType forall)
    {
        var varsArray = new JsonArray();
        foreach (var v in forall.BoundVars)
        {
            varsArray.Add(v);
        }

        return new JsonObject
        {
            ["kind"] = "forall",
            ["boundVars"] = varsArray,
            ["body"] = Serialize(forall.Body),
        };
    }

    private static JsonObject SerializeConstrainedVar(ZType.ZConstrainedVar cv)
    {
        var kindsArray = new JsonArray();
        foreach (var k in cv.AllowedKinds.OrderBy(k => k))
        {
            kindsArray.Add(k.ToString());
        }

        return new JsonObject
        {
            ["kind"] = "constrained",
            ["id"] = cv.Id,
            ["allowedKinds"] = kindsArray,
        };
    }

    private static ZType DeserializePrimitive(JsonObject obj)
    {
        var primitiveKindStr = obj["primitiveKind"]?.GetValue<string>()
            ?? throw new ArgumentException("Missing 'primitiveKind' field in primitive ZType JSON");

        if (!Enum.TryParse<PrimitiveKind>(primitiveKindStr, out var primitiveKind))
        {
            throw new ArgumentException($"Unknown PrimitiveKind: {primitiveKindStr}");
        }

        return new ZType.ZPrimitiveType(primitiveKind);
    }

    private static ZType DeserializeTypeVar(JsonObject obj)
    {
        var id = obj["id"]?.GetValue<int>()
            ?? throw new ArgumentException("Missing 'id' field in typeVar ZType JSON");

        return new ZType.ZTypeVar(id);
    }

    private static ZType DeserializeFuncType(JsonObject obj)
    {
        var paramsNode = obj["params"] as JsonArray
            ?? throw new ArgumentException("Missing or invalid 'params' field in fn ZType JSON");

        var returnNode = obj["return"]
            ?? throw new ArgumentException("Missing 'return' field in fn ZType JSON");

        var paramTypes = new List<ZType>(paramsNode.Count);
        foreach (var p in paramsNode)
        {
            paramTypes.Add(Deserialize(p
                ?? throw new ArgumentException("Null element in 'params' array")));
        }

        return new ZType.ZFuncType(paramTypes, Deserialize(returnNode));
    }

    private static ZType DeserializeNamedType(JsonObject obj)
    {
        var name = obj["name"]?.GetValue<string>()
            ?? throw new ArgumentException("Missing 'name' field in named ZType JSON");

        var argsNode = obj["typeArgs"] as JsonArray
            ?? throw new ArgumentException("Missing or invalid 'typeArgs' field in named ZType JSON");

        var typeArgs = new List<ZType>(argsNode.Count);
        foreach (var arg in argsNode)
        {
            typeArgs.Add(Deserialize(arg
                ?? throw new ArgumentException("Null element in 'typeArgs' array")));
        }

        return new ZType.ZNamedType(name, typeArgs);
    }

    private static ZType DeserializeForAllType(JsonObject obj)
    {
        var varsNode = obj["boundVars"] as JsonArray
            ?? throw new ArgumentException("Missing or invalid 'boundVars' field in forall ZType JSON");

        var bodyNode = obj["body"]
            ?? throw new ArgumentException("Missing 'body' field in forall ZType JSON");

        var boundVars = new List<int>(varsNode.Count);
        foreach (var v in varsNode)
        {
            boundVars.Add(v?.GetValue<int>()
                ?? throw new ArgumentException("Null element in 'boundVars' array"));
        }

        return new ZType.ZForAllType(boundVars, Deserialize(bodyNode));
    }

    private static ZType DeserializeConstrainedVar(JsonObject obj)
    {
        var id = obj["id"]?.GetValue<int>()
            ?? throw new ArgumentException("Missing 'id' field in constrained ZType JSON");

        var kindsNode = obj["allowedKinds"] as JsonArray
            ?? throw new ArgumentException("Missing or invalid 'allowedKinds' field in constrained ZType JSON");

        var allowedKinds = new HashSet<PrimitiveKind>();
        foreach (var k in kindsNode)
        {
            var kindStr = k?.GetValue<string>()
                ?? throw new ArgumentException("Null element in 'allowedKinds' array");

            if (!Enum.TryParse<PrimitiveKind>(kindStr, out var primitiveKind))
            {
                throw new ArgumentException($"Unknown PrimitiveKind: {kindStr}");
            }

            allowedKinds.Add(primitiveKind);
        }

        return new ZType.ZConstrainedVar(id, allowedKinds);
    }
}
