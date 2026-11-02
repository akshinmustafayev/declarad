using Declarad.Config.Modules;
using Declarad.Directory;
using Declarad.Hcl;
using Declarad.Hcl.Syntax;
using Declarad.Hcl.Values;
using Declarad.Resources;
using Declarad.Resources.DataSources;
using Declarad.Resources.Schema;
using Attribute = Declarad.Hcl.Syntax.Attribute;

namespace Declarad.Config
{
    /// <summary>
    /// One module instance: its variables, locals, outputs, resources, data sources and module calls. Resolves
    /// references for expressions evaluated in this module.
    /// </summary>
    internal sealed class ModuleScope : IReferenceResolver
    {
        private const int MaxInstances = 100_000;
        private static readonly string[] ResourceMeta = { "for_each", "count", "depends_on", "connection" };
        private static readonly string[] ModuleMeta = { "source", "version", "for_each", "count", "depends_on", "connections" };
        private static readonly string[] ConnectionAttributes =
            { "server", "port", "protocol", "auth", "username", "password", "domain", "base_dn", "forest_base_dn", "insecure_skip_verify", "ca_cert_dir", "timeout" };

        private readonly ConfigLoader _l;
        public string Dir { get; }
        public string Prefix { get; }
        public ModuleScope? Parent { get; }
        public ModuleInst? Call { get; }
        public int Depth { get; }
        /// <summary>Root module only: connection blocks by name ("default" for the unlabeled one).</summary>
        public Dictionary<string, Block> ConnectionBlocks { get; } = new(StringComparer.Ordinal);

        /// <summary>Connection names visible in this module, mapped to root connection names.</summary>
        public Dictionary<string, string> ConnectionMap { get; } = new(StringComparer.Ordinal);

        /// <summary>Path of module calls from the root, e.g. "team.sub" (used to find installed remote modules).</summary>
        public string ModuleKey { get; init; } = "";

        private readonly Dictionary<string, Block> _variables = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (Value Value, HashSet<string> Deps)> _variableValues = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Attribute> _locals = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (Value Value, HashSet<string> Deps)> _localValues = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Block> _outputs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (Value Value, HashSet<string> Deps)> _outputValues = new(StringComparer.Ordinal);
        private readonly HashSet<string> _inProgress = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Type, string Name), ResourceBlock> _resources = new();
        private readonly Dictionary<(string Type, string Name), DataBlock> _data = new();
        private readonly Dictionary<string, ModuleCall> _modules = new(StringComparer.Ordinal);
        private readonly List<Block> _moved = new();
        private readonly List<Block> _imports = new();
        private HashSet<string>? _extraDeps;

        public ModuleScope(ConfigLoader loader, string dir, string prefix, ModuleScope? parent, ModuleInst? call, int depth)
        {
            _l = loader;
            Dir = dir;
            Prefix = prefix;
            Parent = parent;
            Call = call;
            Depth = depth;
        }

        private bool IsRoot => Parent == null;

        public Value Eval(Expr expr) => new Evaluator(this, _l.Functions).Evaluate(expr);

        public Value? EvalOptional(Attribute? attr)
        {
            if (attr == null)
            {
                return null;
            }
            var v = Eval(attr.Expr);
            return v.IsNull ? null : v;
        }

        /// <summary>Evaluates without leaking dependencies into the caller (for_each/count, lifecycle settings).</summary>
        private Value Isolated(Func<Value> compute, out HashSet<string> deps, Instance? instance = null)
        {
            var frame = new ConfigLoader.Frame { Collector = true, Instance = instance };
            _l.Frames.Push(frame);
            try
            {
                return compute();
            }
            finally
            {
                _l.Frames.Pop();
                deps = frame.Deps;
            }
        }

        public ModuleScope CreateChild(ModuleCall call, ModuleInst inst, string prefix)
        {
            if (Depth + 1 > ConfigLoader.MaxModuleDepth)
            {
                throw new DeclaradException($"Modules are nested more than {ConfigLoader.MaxModuleDepth} levels deep; is a module calling itself?", call.Block.Pos);
            }
            var source = call.Block.Body.GetAttribute("source")!;
            string sourceText = ((LiteralExpr)source.Expr).Value is StringValue s ? s.V : "";
            string key = (ModuleKey.Length > 0 ? ModuleKey + "." : "") + call.Label;
            string dir = _l.ResolveModuleDir(sourceText, Dir, key, source.Pos);
            var child = new ModuleScope(_l, dir, prefix, this, inst, Depth + 1) { ModuleKey = key };
            child.Register(_l.ParseModule(child.Dir, source.Pos));

            // Connections: inherited by name, or mapped explicitly with connections = { child = connection.parent }.
            foreach (var (name, target) in ConnectionMap)
            {
                child.ConnectionMap[name] = target;
            }
            if (call.Block.Body.GetAttribute("connections") is { } mapping)
            {
                if (mapping.Expr is not ObjectExpr obj)
                {
                    throw new DeclaradException("connections must be an object: connections = { default = connection.eu }", mapping.Pos);
                }
                foreach (var (k, v) in obj.Items)
                {
                    string childName = k is LiteralExpr { Value: StringValue ks } ? ks.V : k is ScopeTraversalExpr { Traversal.Count: 0 } kid ? kid.Root
                        : throw new DeclaradException("connections keys must be names.", k.Pos);
                    child.ConnectionMap[childName] = ResolveConnectionReference(v);
                }
            }

            foreach (var attr in call.Block.Body.Attributes.Where(a => !ModuleMeta.Contains(a.Name)))
            {
                if (!child._variables.ContainsKey(attr.Name))
                {
                    _l.Error($"Module {call.Describe} has no variable '{attr.Name}'{ConfigLoader.Suggest(attr.Name, child._variables.Keys)}.", attr.Pos);
                }
            }
            foreach (var (name, block) in child._variables)
            {
                if (block.Body.GetAttribute("default") == null && call.Block.Body.GetAttribute(name) == null)
                {
                    _l.Error($"Module {call.Describe} requires variable '{name}'.", call.Block.Pos);
                }
            }
            return child;
        }

        /// <summary>Resolves <c>connection.eu</c> (or <c>connection.default</c>) to a root connection name.</summary>
        private string ResolveConnectionReference(Expr expr)
        {
            if (expr is not ScopeTraversalExpr { Root: "connection", Traversal: [GetAttr name] })
            {
                throw new DeclaradException("Expected a connection reference such as connection.eu.", expr.Pos);
            }
            return ConnectionMap.TryGetValue(name.Name, out var target)
                ? target
                : throw new DeclaradException($"Connection '{name.Name}' is not available here{(IsRoot ? "" : "; pass it with connections = { ... } on the module call")}. Known: {string.Join(", ", ConnectionMap.Keys)}.", expr.Pos);
        }

        /// <summary>The root connection name a resource or data block uses.</summary>
        public string ConnectionOf(Block block)
        {
            if (block.Body.GetAttribute("connection") is { } attr)
            {
                return ResolveConnectionReference(attr.Expr);
            }
            return ConnectionMap.TryGetValue(State.StateFile.DefaultConnection, out var target)
                ? target
                : throw new DeclaradException($"There is no default connection{(IsRoot ? "" : " in this module")}; set connection = connection.<name>.", block.Pos);
        }

        // ---- Registration ----------------------------------------------------------------------------

        public void Register(List<Body> bodies)
        {
            foreach (var body in bodies)
            {
                foreach (var attr in body.Attributes)
                {
                    _l.Error($"Unexpected top-level attribute '{attr.Name}'. Attributes belong inside a block.", attr.Pos);
                }
                foreach (var block in body.Blocks)
                {
                    RegisterBlock(block);
                }
            }
            foreach (var b in _resources.Values)
            {
                ValidateResourceShape(b);
            }
            foreach (var block in _data.Values.Select(d => d.Block).Concat(_modules.Values.Select(m => m.Block)).Concat(_moved).Concat(_imports))
            {
                foreach (var nested in block.Body.Blocks)
                {
                    _l.Error($"Unexpected block '{nested.Type}' inside {block.Type}.", nested.Pos);
                }
            }
            foreach (var d in _data.Values)
            {
                foreach (var attr in d.Block.Body.Attributes.Where(a => d.Type.FindArgument(a.Name) == null && !ResourceMeta.Contains(a.Name)))
                {
                    _l.Error($"data.{d.Type.Name} has no argument '{attr.Name}'{ConfigLoader.Suggest(attr.Name, d.Type.Arguments.Select(a => a.Name))}.", attr.Pos);
                }
                CheckExpansionMeta(d);
            }
            foreach (var m in _modules.Values)
            {
                CheckExpansionMeta(m);
            }
        }

        private void RegisterBlock(Block block)
        {
            string? SingleLabel(string usage)
            {
                if (block.Labels.Count != 1 || !HclWriter.IsIdentifier(block.Labels[0]))
                {
                    _l.Error($"A {block.Type} block needs one label that is a valid identifier: {usage}", block.Pos);
                    return null;
                }
                return block.Labels[0];
            }

            switch (block.Type)
            {
                case "connection":
                    if (!IsRoot)
                    {
                        _l.Error("A connection block is only allowed in the root module; child modules use the root connection.", block.Pos);
                        return;
                    }
                    if (block.Labels.Count > 1 || (block.Labels.Count == 1 && !HclWriter.IsIdentifier(block.Labels[0])))
                    {
                        _l.Error("A connection block takes at most one label: connection \"name\" { ... }", block.Pos);
                        return;
                    }
                    string connectionName = block.Labels.Count == 1 ? block.Labels[0] : State.StateFile.DefaultConnection;
                    if (!ConnectionBlocks.TryAdd(connectionName, block))
                    {
                        _l.Error($"Connection '{connectionName}' is declared more than once.", block.Pos);
                    }
                    ConnectionMap[connectionName] = connectionName;
                    foreach (var attr in block.Body.Attributes.Where(a => !ConnectionAttributes.Contains(a.Name)))
                    {
                        _l.Error($"Unknown connection attribute '{attr.Name}'{ConfigLoader.Suggest(attr.Name, ConnectionAttributes)}.", attr.Pos);
                    }
                    return;

                case "variable":
                    if (SingleLabel("variable \"name\" { ... }") is { } varName && !_variables.TryAdd(varName, block))
                    {
                        _l.Error($"Variable '{varName}' is declared more than once.", block.Pos);
                    }
                    CheckAttributes(block, "variable", "default", "description", "type", "sensitive", "nullable");
                    foreach (var nested in block.Body.Blocks)
                    {
                        if (nested.Type != "validation")
                        {
                            _l.Error($"Unexpected block '{nested.Type}' in variable; only 'validation' is allowed.", nested.Pos);
                        }
                        CheckCondition(nested);
                    }
                    return;

                case "locals":
                    foreach (var nested in block.Body.Blocks)
                    {
                        _l.Error($"Unexpected block '{nested.Type}' inside locals.", nested.Pos);
                    }
                    foreach (var attr in block.Body.Attributes)
                    {
                        if (!_locals.TryAdd(attr.Name, attr))
                        {
                            _l.Error($"Local value '{attr.Name}' is defined more than once.", attr.Pos);
                        }
                    }
                    return;

                case "output":
                    if (SingleLabel("output \"name\" { value = ... }") is { } outName && !_outputs.TryAdd(outName, block))
                    {
                        _l.Error($"Output '{outName}' is declared more than once.", block.Pos);
                    }
                    CheckAttributes(block, "output", "value", "description", "sensitive", "depends_on");
                    if (block.Body.GetAttribute("value") == null)
                    {
                        _l.Error("An output needs a value.", block.Pos);
                    }
                    foreach (var nested in block.Body.Blocks)
                    {
                        if (nested.Type != "precondition")
                        {
                            _l.Error($"Unexpected block '{nested.Type}' in output; only 'precondition' is allowed.", nested.Pos);
                        }
                        CheckCondition(nested);
                    }
                    return;

                case "module":
                {
                    if (SingleLabel("module \"name\" { source = \"./modules/x\" }") is not { } name)
                    {
                        return;
                    }
                    var source = block.Body.GetAttribute("source");
                    if (source?.Expr is not LiteralExpr { Value: StringValue sv })
                    {
                        _l.Error("A module block needs source = \"./path\" (a literal string).", block.Pos);
                        return;
                    }
                    try
                    {
                        ModuleSource.Parse(sv.V);
                    }
                    catch (DeclaradException ex)
                    {
                        _l.AddError(ex, source.Pos);
                        return;
                    }
                    if (!_modules.TryAdd(name, new ModuleCall { Scope = this, Block = block }))
                    {
                        _l.Error($"Module '{name}' is declared more than once.", block.Pos);
                    }
                    return;
                }

                case "data":
                {
                    if (block.Labels.Count != 2)
                    {
                        _l.Error("A data block needs two labels: data \"type\" \"name\" { ... }", block.Pos);
                        return;
                    }
                    var type = DataSourceRegistry.Find(block.Labels[0]);
                    if (type == null)
                    {
                        _l.Error($"Unknown data source '{block.Labels[0]}'. Available: {string.Join(", ", DataSourceRegistry.All.Select(d => d.Name))}.", block.Pos);
                        return;
                    }
                    if (!_data.TryAdd((type.Name, block.Labels[1]), new DataBlock { Scope = this, Block = block, Type = type }))
                    {
                        _l.Error($"data.{type.Name}.{block.Labels[1]} is declared more than once.", block.Pos);
                    }
                    return;
                }

                case "declarad":
                    if (!IsRoot)
                    {
                        _l.Error("The declarad block is only allowed in the root module.", block.Pos);
                    }
                    if (block.Labels.Count > 0)
                    {
                        _l.Error("The declarad block takes no labels.", block.Pos);
                    }
                    return;

                case "moved":
                    CheckAttributes(block, "moved", "from", "to");
                    if (Require(block, "from", "to"))
                    {
                        _moved.Add(block);
                    }
                    return;

                case "import":
                    if (!IsRoot)
                    {
                        _l.Error("import blocks are only allowed in the root module.", block.Pos);
                        return;
                    }
                    CheckAttributes(block, "import", "to", "id");
                    if (Require(block, "to", "id"))
                    {
                        _imports.Add(block);
                    }
                    return;

                case "resource" when block.Labels.Count == 2:
                    RegisterResource(block, block.Labels[0], block.Labels[1]);
                    return;

                default:
                    if (ResourceRegistry.Find(block.Type) == null)
                    {
                        _l.Error($"Unknown block type '{block.Type}'{ConfigLoader.Suggest(block.Type, ResourceRegistry.All.Select(t => t.Name))}. " +
                                 $"Expected connection, variable, locals, output, module, data, moved, import or a resource type ({string.Join(", ", ResourceRegistry.All.Select(t => t.Name))}).", block.Pos);
                        return;
                    }
                    if (SingleLabel($"{block.Type} \"name\" {{ ... }}") is { } label)
                    {
                        RegisterResource(block, block.Type, label);
                    }
                    return;
            }
        }

        private void RegisterResource(Block block, string typeName, string label)
        {
            var type = ResourceRegistry.Find(typeName);
            if (type == null)
            {
                _l.Error($"Unknown resource type '{typeName}'.", block.Pos);
                return;
            }
            if (!HclWriter.IsIdentifier(label))
            {
                _l.Error($"'{label}' is not a valid resource name.", block.Pos);
                return;
            }
            if (!_resources.TryAdd((type.Name, label), new ResourceBlock { Scope = this, Block = block, Type = type }))
            {
                _l.Error($"Resource {type.Name}.{label} is declared more than once.", block.Pos);
            }
        }

        private void CheckAttributes(Block block, string what, params string[] allowed)
        {
            foreach (var attr in block.Body.Attributes.Where(a => !allowed.Contains(a.Name)))
            {
                _l.Error($"Unknown {what} attribute '{attr.Name}'{ConfigLoader.Suggest(attr.Name, allowed)}.", attr.Pos);
            }
        }

        private bool Require(Block block, params string[] names)
        {
            bool ok = true;
            foreach (string name in names.Where(n => block.Body.GetAttribute(n) == null))
            {
                _l.Error($"A {block.Type} block needs '{name}'.", block.Pos);
                ok = false;
            }
            return ok;
        }

        private void CheckCondition(Block block)
        {
            if (block.Body.GetAttribute("condition") == null || block.Body.GetAttribute("error_message") == null)
            {
                _l.Error($"A {block.Type} block needs condition and error_message.", block.Pos);
            }
        }

        private void CheckExpansionMeta(Expandable e)
        {
            if (e.ForEach != null && e.Count != null)
            {
                _l.Error($"{e.Describe}: for_each and count cannot be used together.", e.Block.Pos);
            }
        }

        private void ValidateResourceShape(ResourceBlock rb)
        {
            CheckExpansionMeta(rb);
            foreach (var attr in rb.Block.Body.Attributes)
            {
                if (rb.Type.Find(attr.Name) == null && !ResourceMeta.Contains(attr.Name))
                {
                    var names = rb.Type.Schema.Select(s => s.Name).Concat(ResourceMeta);
                    _l.Error($"{rb.Type.Name} has no attribute '{attr.Name}'{ConfigLoader.Suggest(attr.Name, names)}.", attr.Pos);
                }
            }
            if (rb.Block.Body.Blocks.Count(b => b.Type == "lifecycle") > 1)
            {
                _l.Error($"{rb.Describe} has more than one lifecycle block.", rb.Block.Pos);
            }
            foreach (var nested in rb.Block.Body.Blocks)
            {
                if (nested.Type != "lifecycle")
                {
                    _l.Error($"Unexpected block '{nested.Type}' inside {rb.Type.Name}; only 'lifecycle' is allowed.", nested.Pos);
                    continue;
                }
                foreach (var attr in nested.Body.Attributes.Where(a => a.Name is not ("prevent_destroy" or "ignore_changes")))
                {
                    _l.Error($"Unknown lifecycle attribute '{attr.Name}'. Expected prevent_destroy or ignore_changes.", attr.Pos);
                }
                foreach (var condition in nested.Body.Blocks)
                {
                    if (condition.Type != "precondition")
                    {
                        _l.Error($"Unexpected block '{condition.Type}' in lifecycle; only 'precondition' is allowed.", condition.Pos);
                    }
                    CheckCondition(condition);
                }
            }
        }

        // ---- Evaluation of the whole module ------------------------------------------------------------

        public void EvaluateAll(Configuration config)
        {
            foreach (var (name, variable) in _variables)
            {
                if (!IsRoot && variable.Body.GetAttribute("default") == null && Call!.Owner.Block.Body.GetAttribute(name) == null)
                {
                    continue; // Already reported once per module call.
                }
                Try(() => ResolveVariable(name, variable.Pos), variable.Pos);
            }
            if (IsRoot)
            {
                foreach (string name in (_l.Options.Vars ?? new Dictionary<string, string>()).Keys.Where(n => !_variables.ContainsKey(n)))
                {
                    _l.Error($"--var {name}: there is no variable \"{name}\" in the root module{ConfigLoader.Suggest(name, _variables.Keys)}.", new SourcePos("<command line>", 0, 0));
                }
            }
            foreach (var block in _resources.Values)
            {
                foreach (var inst in TryInstances(block).Cast<ResourceInst>())
                {
                    EvaluateInstance(inst);
                    config.Resources.Add(inst.Result);
                }
            }
            foreach (var block in _data.Values)
            {
                foreach (var inst in TryInstances(block).Cast<DataInst>())
                {
                    Try(() => EvalData(inst), block.Block.Pos);
                }
            }
            foreach (var call in _modules.Values)
            {
                var instances = TryInstances(call).Cast<ModuleInst>().ToList();
                if (instances.Count == 0 && call.IsExpanded)
                {
                    // count = 0 / empty for_each: still check the module and its arguments, as Terraform does.
                    Try(() => CreateChild(call, new ModuleInst { Owner = call }, (Prefix.Length > 0 ? Prefix + "." : "") + $"module.{call.Label}"), call.Block.Pos);
                }
                foreach (var inst in instances)
                {
                    inst.Child.EvaluateAll(config);
                }
            }
            foreach (var (name, block) in _outputs)
            {
                try
                {
                    var value = ResolveOutput(name, block.Pos);
                    if (IsRoot)
                    {
                        bool sensitive = EvalOptional(block.Body.GetAttribute("sensitive")) is { } s && Evaluator.ToBool(s, block.Pos);
                        string? description = EvalOptional(block.Body.GetAttribute("description")) is { } d ? V.Str(d) : null;
                        config.Outputs.Add(new OutputValue(name, value, sensitive, description));
                    }
                }
                catch (DeclaradException ex)
                {
                    _l.AddError(ex, block.Pos, $"output.{name}");
                }
            }
            foreach (var moved in _moved)
            {
                try
                {
                    string from = AddressOf(moved.Body.GetAttribute("from")!.Expr);
                    string to = AddressOf(moved.Body.GetAttribute("to")!.Expr);
                    config.Moves.Add(new MoveSpec(from, to, moved.Pos));
                }
                catch (DeclaradException ex)
                {
                    _l.AddError(ex, moved.Pos);
                }
            }
            foreach (var import in _imports)
            {
                try
                {
                    string to = AddressOf(import.Body.GetAttribute("to")!.Expr);
                    var id = Eval(import.Body.GetAttribute("id")!.Expr);
                    if (!id.IsUnknown)
                    {
                        config.Imports.Add(new ImportSpec(to, Evaluator.ToStringValue(id, import.Pos, "import id"), import.Pos));
                    }
                }
                catch (DeclaradException ex)
                {
                    _l.AddError(ex, import.Pos);
                }
            }
        }

        private void Try(Action action, SourcePos pos)
        {
            try
            {
                action();
            }
            catch (DeclaradException ex)
            {
                _l.AddError(ex, pos);
            }
        }

        private List<Instance> TryInstances(Expandable e)
        {
            try
            {
                return Instances(e);
            }
            catch (DeclaradException ex)
            {
                _l.AddError(ex, e.Block.Pos, e.Describe);
                return new List<Instance>();
            }
        }

        /// <summary>Turns a reference such as <c>user.staff["jdoe"]</c> or <c>module.team.user.x</c> into an address string.</summary>
        private string AddressOf(Expr expr)
        {
            if (expr is not ScopeTraversalExpr s)
            {
                throw new DeclaradException("Expected a resource or module reference, e.g. user.jdoe or module.team.", expr.Pos);
            }
            var parts = new System.Text.StringBuilder(s.Root);
            foreach (var step in s.Traversal)
            {
                switch (step)
                {
                    case GetAttr g:
                        parts.Append('.').Append(g.Name);
                        break;
                    case Hcl.Syntax.Index { Key: LiteralExpr { Value: var key } } when key is StringValue or NumberValue:
                        parts.Append(ResourceInstance.FormatKey(key));
                        break;
                    default:
                        throw new DeclaradException("Instance keys in addresses must be literals, e.g. user.staff[\"jdoe\"].", step.Pos);
                }
            }
            return (Prefix.Length > 0 ? Prefix + "." : "") + parts;
        }

        // ---- Expansion ---------------------------------------------------------------------------------

        public List<Instance> Instances(Expandable e)
        {
            if (e.Instances != null)
            {
                return e.Instances;
            }
            if (!e.IsExpanded)
            {
                e.Instances = new List<Instance> { e.NewInstance(null, Value.Null, Value.Null) };
                return e.Instances;
            }
            var attr = e.ForEach ?? e.Count!;
            if (e.Expanding)
            {
                throw new DeclaradException($"{attr.Name} of {e.Describe} refers to the block itself.", attr.Pos);
            }
            e.Expanding = true;
            Value collection;
            try
            {
                collection = Isolated(() => Eval(attr.Expr), out var deps);
                e.ExpansionDeps.UnionWith(deps);
            }
            finally
            {
                e.Expanding = false;
            }

            var instances = new List<Instance>();
            if (collection.ContainsUnknown())
            {
                e.UnknownExpansion = true;
                _l.Warnings.Add($"{e.Describe}: {attr.Name} depends on values read from the directory, so its instances were not validated offline.");
            }
            else if (e.Count != null)
            {
                decimal n = Evaluator.ToNumber(collection, attr.Pos);
                if (n < 0 || decimal.Truncate(n) != n || n > MaxInstances)
                {
                    throw new DeclaradException($"count must be a whole number between 0 and {MaxInstances}, got {NumberValue.Format(n)}.", attr.Pos);
                }
                for (int i = 0; i < n; i++)
                {
                    instances.Add(e.NewInstance(Value.From(i), Value.Null, Value.Null));
                }
            }
            else
            {
                switch (collection)
                {
                    case ObjectValue obj:
                        foreach (var (key, value) in obj.Fields)
                        {
                            instances.Add(e.NewInstance(Value.From(key), Value.From(key), value));
                        }
                        break;
                    case ListValue list:
                        foreach (var item in list.Items.Distinct())
                        {
                            if (item is not StringValue s)
                            {
                                throw new DeclaradException(
                                    $"for_each over a list needs strings, got a {item.TypeName}. To iterate over objects (e.g. csvdecode rows) build a map: {{ for u in local.rows : u.sam => u }}",
                                    attr.Pos);
                            }
                            instances.Add(e.NewInstance(item, item, item));
                        }
                        break;
                    case NullValue:
                        throw new DeclaradException("for_each must not be null.", attr.Pos);
                    default:
                        throw new DeclaradException($"for_each must be a map or a set of strings, got {collection.TypeName}.", attr.Pos);
                }
            }
            e.Instances = instances;
            return instances;
        }

        private static Instance FindInstance(Expandable e, List<Instance> instances, Value key, SourcePos pos)
        {
            return instances.FirstOrDefault(i => i.Key != null && (i.Key.Equals(key) || (i.Key is StringValue s && key is NumberValue n && s.V == n.ToHcl()) || (i.Key is NumberValue && key is StringValue ks && i.Key.ToHcl() == ks.V)))
                ?? throw new DeclaradException($"{e.Describe} has no instance with key {(key is StringValue sk ? HclWriter.Quote(sk.V) : key.ToHcl())}.", pos);
        }

        /// <summary>Value of a whole expanded block: a map for for_each, a list for count.</summary>
        private static Value Collection(Expandable e, List<Instance> instances, Func<Instance, Value> project)
        {
            if (e.UnknownExpansion)
            {
                return Value.Unknown;
            }
            return e.Count != null
                ? Value.From(instances.Select(project).ToList())
                : new ObjectValue(instances.Select(i => new KeyValuePair<string, Value>(V.Str(i.Key!)!, project(i))));
        }

        // ---- Variables, locals, outputs ---------------------------------------------------------------

        private Value ResolveVariable(string name, SourcePos pos)
        {
            if (_variableValues.TryGetValue(name, out var cached))
            {
                _l.Replay(cached.Deps);
                return cached.Value;
            }
            if (!_variables.TryGetValue(name, out var block))
            {
                throw new DeclaradException($"Undeclared variable '{name}'. Add a variable \"{name}\" {{ }} block{(IsRoot ? "" : " to the module")}.", pos);
            }
            if (!_inProgress.Add("var." + name))
            {
                throw new DeclaradException($"Variable '{name}' refers to itself.", pos);
            }
            try
            {
                var body = block.Body;
                var typeAttr = body.GetAttribute("type");
                var type = typeAttr != null ? TypeConstraint.Parse(typeAttr.Expr, Eval) : null;
                var defaultAttr = body.GetAttribute("default");
                Value? defaultValue = defaultAttr != null ? Eval(defaultAttr.Expr) : null;
                bool nullable = EvalOptional(body.GetAttribute("nullable")) is not { } nb || Evaluator.ToBool(nb, block.Pos);

                Value? value = null;
                HashSet<string> deps = new(StringComparer.Ordinal);
                if (IsRoot)
                {
                    var overrides = _l.Options.Vars ?? new Dictionary<string, string>();
                    string? raw = overrides.TryGetValue(name, out var o) ? o : (_l.Options.GetEnv ?? Environment.GetEnvironmentVariable)("DECLARAD_VAR_" + name);
                    if (raw != null)
                    {
                        // Like Terraform: primitives are taken as text and converted; only collections are parsed as HCL.
                        bool structured = type != null
                            ? type is not TypeConstraint.Primitive
                            : defaultValue is ListValue or ObjectValue;
                        value = structured
                            ? new Evaluator(NoReferences.Instance, _l.Functions).Evaluate(Parser.ParseExpression(raw, new SourcePos($"<var {name}>", 1, 1)))
                            : Value.From(raw);
                        if (type == null && defaultValue is NumberValue or BoolValue)
                        {
                            type = new TypeConstraint.Primitive(defaultValue.TypeName);
                        }
                    }
                }
                else if (Call!.Owner.Block.Body.GetAttribute(name) is { } input)
                {
                    value = _l.Collect(() => Parent!.Eval(input.Expr), out deps, Call);
                }

                if (value != null && value.IsNull && !nullable && defaultValue == null)
                {
                    throw new DeclaradException($"Variable '{name}' must not be null.", block.Pos);
                }
                if (value == null || (value.IsNull && !nullable))
                {
                    value = defaultValue ?? (value != null && nullable ? value : null);
                }
                if (value == null)
                {
                    throw new DeclaradException(IsRoot
                        ? $"Variable '{name}' has no value; pass --var {name}=... or set DECLARAD_VAR_{name}."
                        : $"Module {Call!.Owner.Describe} requires variable '{name}'.", block.Pos);
                }
                if (value.IsNull && !nullable)
                {
                    throw new DeclaradException($"Variable '{name}' must not be null.", block.Pos);
                }
                try
                {
                    value = (type ?? TypeConstraint.Any).Convert(value, $"var.{name}");
                }
                catch (DeclaradException ex)
                {
                    throw new DeclaradException($"Invalid value for variable '{name}': {ex.Message}", block.Pos);
                }

                if (EvalOptional(body.GetAttribute("sensitive")) is { } sensitive && Evaluator.ToBool(sensitive, block.Pos))
                {
                    deps.Add(ConfigLoader.SensitiveMarker);
                    _l.Replay(deps);
                }
                _variableValues[name] = (value, deps);
                foreach (var validation in body.Blocks.Where(b => b.Type == "validation"))
                {
                    CheckCondition(validation, $"Invalid value for variable '{name}'", null);
                }
                return value;
            }
            finally
            {
                _inProgress.Remove("var." + name);
            }
        }

        private void CheckCondition(Block block, string what, Instance? instance)
        {
            var conditionAttr = block.Body.GetAttribute("condition")!;
            var condition = Isolated(() => Eval(conditionAttr.Expr), out _, instance);
            if (condition.IsUnknown || Evaluator.ToBool(condition, conditionAttr.Pos))
            {
                return;
            }
            var message = Isolated(() => Eval(block.Body.GetAttribute("error_message")!.Expr), out _, instance);
            throw new DeclaradException($"{what}: {(message.IsUnknown ? "condition failed" : V.Str(message))}", conditionAttr.Pos);
        }

        private Value ResolveLocal(string name, SourcePos pos)
        {
            if (_localValues.TryGetValue(name, out var cached))
            {
                _l.Replay(cached.Deps);
                return cached.Value;
            }
            if (!_locals.TryGetValue(name, out var attr))
            {
                throw new DeclaradException($"Unknown local value '{name}'{ConfigLoader.Suggest(name, _locals.Keys)}.", pos);
            }
            if (!_inProgress.Add("local." + name))
            {
                throw new DeclaradException($"Local value '{name}' refers to itself.", pos);
            }
            try
            {
                var value = _l.Collect(() => Eval(attr.Expr), out var deps);
                _localValues[name] = (value, deps);
                return value;
            }
            finally
            {
                _inProgress.Remove("local." + name);
            }
        }

        public Value ResolveOutput(string name, SourcePos pos)
        {
            if (_outputValues.TryGetValue(name, out var cached))
            {
                _l.Replay(cached.Deps);
                return cached.Value;
            }
            if (!_outputs.TryGetValue(name, out var block))
            {
                throw new DeclaradException($"Module has no output '{name}'{ConfigLoader.Suggest(name, _outputs.Keys)}.", pos);
            }
            if (!_inProgress.Add("output." + name))
            {
                throw new DeclaradException($"Output '{name}' refers to itself.", pos);
            }
            try
            {
                var value = _l.Collect(() => Eval(block.Body.GetAttribute("value")!.Expr), out var deps);
                if (block.Body.GetAttribute("depends_on") is { } dependsOn)
                {
                    var extra = AddressesOf(dependsOn).ToList();
                    deps.UnionWith(extra);
                    _l.Replay(extra);
                }
                bool declaredSensitive = EvalOptional(block.Body.GetAttribute("sensitive")) is { } s && Evaluator.ToBool(s, block.Pos);
                if (deps.Contains(ConfigLoader.SensitiveMarker) && !declaredSensitive)
                {
                    throw new DeclaradException($"Output '{name}' refers to sensitive values; add sensitive = true to the output.", block.Pos);
                }
                if (declaredSensitive)
                {
                    deps.Add(ConfigLoader.SensitiveMarker);
                    _l.Replay(new[] { ConfigLoader.SensitiveMarker });
                }
                _outputValues[name] = (value, deps);
                foreach (var precondition in block.Body.Blocks.Where(b => b.Type == "precondition"))
                {
                    CheckCondition(precondition, $"Output '{name}' precondition failed", null);
                }
                return value;
            }
            finally
            {
                _inProgress.Remove("output." + name);
            }
        }

        private Value ModuleOutputs(ModuleInst inst, SourcePos pos) =>
            new ObjectValue(inst.Child._outputs.Keys.Select(o => new KeyValuePair<string, Value>(o, inst.Child.ResolveOutput(o, pos))));

        // ---- Resources ------------------------------------------------------------------------------

        private HashSet<string> ExtraDeps
        {
            get
            {
                if (_extraDeps != null)
                {
                    return _extraDeps;
                }
                _extraDeps = new HashSet<string>(Parent?.ExtraDeps ?? new HashSet<string>(), StringComparer.Ordinal);
                if (Call != null)
                {
                    // The module's for_each/count (and so each.value) may come from resources.
                    _extraDeps.UnionWith(ConfigLoader.Addresses(Call.Owner.ExpansionDeps));
                }
                if (Call?.Owner.Block.Body.GetAttribute("depends_on") is { } dependsOn)
                {
                    _extraDeps.UnionWith(Parent!.AddressesOf(dependsOn));
                }
                return _extraDeps;
            }
        }

        /// <summary>Resource addresses named by a depends_on list (resources, or every resource of a module).</summary>
        private IEnumerable<string> AddressesOf(Attribute dependsOn)
        {
            if (dependsOn.Expr is not ListExpr list)
            {
                throw new DeclaradException("depends_on must be a list of references, e.g. [ou.sales, module.base].", dependsOn.Pos);
            }
            var result = new List<string>();
            foreach (var item in list.Items)
            {
                if (item is ScopeTraversalExpr { Root: "module", Traversal: [GetAttr m, ..] } && _modules.TryGetValue(m.Name, out var call))
                {
                    foreach (var inst in Instances(call).Cast<ModuleInst>())
                    {
                        result.AddRange(inst.Child.AllResourceAddresses());
                    }
                }
                else if (item is ScopeTraversalExpr { Traversal: [GetAttr target, ..] } s && ResourceRegistry.Find(s.Root) is { } t
                         && _resources.TryGetValue((t.Name, target.Name), out var block))
                {
                    result.AddRange(Instances(block).Cast<ResourceInst>().Select(i => i.Result.Address));
                }
                else if (item is not ScopeTraversalExpr { Root: "data" })
                {
                    throw new DeclaradException("depends_on entries must reference declared resources or modules, e.g. ou.sales or module.base.", item.Pos);
                }
            }
            return result;
        }

        private IEnumerable<string> AllResourceAddresses() =>
            _resources.Values.SelectMany(b => Instances(b).Cast<ResourceInst>().Select(i => i.Result.Address))
                .Concat(_modules.Values.SelectMany(m => Instances(m).Cast<ModuleInst>().SelectMany(i => i.Child.AllResourceAddresses())));

        private void EvaluateInstance(ResourceInst inst)
        {
            var result = inst.Result;
            var type = inst.Block.Type;
            var body = inst.Block.Block.Body;
            int errorsBefore = _l.Errors.Count;

            foreach (var spec in type.Schema)
            {
                var attr = body.GetAttribute(spec.Name);
                if (attr != null)
                {
                    result.AttributePos[spec.Name] = attr.Pos;
                }
                SourcePos pos = attr?.Pos ?? result.Pos;
                try
                {
                    var value = EvalAttr(inst, spec.Name);
                    result.Attributes[spec.Name] = value;
                    if (value.ContainsUnknown())
                    {
                        result.HasUnknowns = true;
                        continue;
                    }
                    if (value.IsNull)
                    {
                        if (spec.Required)
                        {
                            _l.Error($"{result.Address}: attribute '{spec.Name}' is required.", pos);
                        }
                        continue;
                    }
                    if (spec.AllowedValues != null && !spec.AllowedValues.Contains(V.Str(value)))
                    {
                        _l.Error($"{result.Address}: {spec.Name} must be one of {string.Join(", ", spec.AllowedValues)}; got '{V.Str(value)}'.", pos);
                    }
                    if (spec.Validate?.Invoke(value) is { } message)
                    {
                        _l.Error($"{result.Address}: {spec.Name} {message}", pos);
                    }
                    if (spec.IsDn && !spec.AcceptsSamAccountName)
                    {
                        foreach (var item in (spec.Type == AttrType.StringList ? V.List(value) : new[] { V.Str(value)! }).Where(i => !Dn.IsValid(i)))
                        {
                            _l.Error($"{result.Address}: '{item}' in {spec.Name} is not a valid distinguished name.", pos);
                        }
                    }
                }
                catch (DeclaradException ex)
                {
                    _l.AddError(ex, pos, result.Address);
                }
            }
            if (_l.Errors.Count > errorsBefore)
            {
                return;
            }

            if (!result.HasUnknowns)
            {
                try
                {
                    type.Normalize(result);
                }
                catch (DeclaradException ex)
                {
                    _l.AddError(ex, result.Pos, result.Address);
                    return;
                }
                foreach (var message in type.Validate(result.Get))
                {
                    _l.Error($"{result.Address}: {message}", result.Pos);
                }
            }
            try
            {
                var dn = EvalAttr(inst, "dn");
                result.Dn = V.Str(dn) ?? "";
                result.HasUnknowns |= dn.IsUnknown;
            }
            catch (DeclaradException ex)
            {
                _l.AddError(ex, result.Pos, result.Address);
            }

            try
            {
                ApplyMetaArguments(inst);
            }
            catch (DeclaradException ex)
            {
                _l.AddError(ex, result.Pos, result.Address);
            }
        }

        private void ApplyMetaArguments(ResourceInst inst)
        {
            var result = inst.Result;
            var body = inst.Block.Block.Body;
            result.DependsOn.UnionWith(ConfigLoader.Addresses(inst.Block.ExpansionDeps));
            result.DependsOn.UnionWith(ExtraDeps);
            if (body.GetAttribute("depends_on") is { } dependsOn)
            {
                result.DependsOn.UnionWith(AddressesOf(dependsOn));
            }
            result.DependsOn.Remove(result.Address);
            result.References.UnionWith(result.DependsOn);

            foreach (var lifecycle in body.Blocks.Where(b => b.Type == "lifecycle"))
            {
                if (lifecycle.Body.GetAttribute("prevent_destroy") is { } pd)
                {
                    var preventDestroy = Isolated(() => Eval(pd.Expr), out _, inst);
                    result.PreventDestroy = preventDestroy is not (NullValue or UnknownValue) && Evaluator.ToBool(preventDestroy, pd.Pos);
                }
                if (lifecycle.Body.GetAttribute("ignore_changes") is { } ic)
                {
                    if (ic.Expr is ScopeTraversalExpr { Root: "all", Traversal.Count: 0 } or LiteralExpr { Value: StringValue { V: "all" } })
                    {
                        result.IgnoreChanges.UnionWith(result.Type.Schema.Select(s => s.Name));
                    }
                    else if (ic.Expr is ListExpr list)
                    {
                        foreach (var item in list.Items)
                        {
                            string? name = item switch
                            {
                                ScopeTraversalExpr { Traversal.Count: 0 } s => s.Root,
                                LiteralExpr { Value: StringValue sv } => sv.V,
                                _ => null,
                            };
                            if (name == null || result.Type.Find(name) == null)
                            {
                                _l.Error($"ignore_changes: '{name ?? "expression"}' is not an attribute of {result.Type.Name}.", item.Pos);
                            }
                            else
                            {
                                result.IgnoreChanges.Add(name);
                            }
                        }
                    }
                    else
                    {
                        _l.Error("ignore_changes must be a list of attribute names, or all.", ic.Pos);
                    }
                }
                if (!result.HasUnknowns)
                {
                    foreach (var precondition in lifecycle.Body.Blocks.Where(b => b.Type == "precondition"))
                    {
                        try
                        {
                            CheckCondition(precondition, $"{result.Address}: precondition failed", inst);
                        }
                        catch (DeclaradException ex)
                        {
                            _l.AddError(ex, precondition.Pos);
                        }
                    }
                }
            }
        }

        private Value EvalAttr(ResourceInst inst, string name)
        {
            if (inst.Memo.TryGetValue(name, out var cached))
            {
                return cached;
            }
            var type = inst.Block.Type;
            if (name == "dn")
            {
                if (type is not AttachmentType && (EvalAttr(inst, "name").IsUnknown || EvalAttr(inst, "path").IsUnknown))
                {
                    return inst.Memo[name] = Value.Unknown;
                }
                if (type is AttachmentType at && at.IdentityAttributes.Any(a => EvalAttr(inst, a).IsUnknown))
                {
                    return inst.Memo[name] = Value.Unknown;
                }
                try
                {
                    return inst.Memo[name] = Value.From(type.ComputeDn(a => EvalAttr(inst, a), _l.Schema(inst.Result.Connection)));
                }
                catch (DeclaradException ex) when (ex.Pos.Line == 0)
                {
                    throw new DeclaradException($"{inst.Result.Address}: cannot compute its DN: {ex.Message}");
                }
            }

            var spec = type.Find(name)
                ?? throw new DeclaradException($"{type.Name} has no attribute '{name}'{ConfigLoader.Suggest(name, type.Schema.Select(s => s.Name).Append("dn"))}.");
            if (!inst.InProgress.Add(name))
            {
                throw new DeclaradException($"Cycle detected while evaluating {inst.Result.Address}.{name}.");
            }

            var frame = new ConfigLoader.Frame { Instance = inst, Attribute = name };
            _l.Frames.Push(frame);
            try
            {
                Value value = Value.Null;
                var attr = inst.Block.Block.Body.GetAttribute(name);
                if (attr != null)
                {
                    value = V.Coerce(Eval(attr.Expr), spec, attr.Pos);
                    if (spec.Normalize != null && !value.IsNull && !value.ContainsUnknown())
                    {
                        value = spec.Normalize(value);
                    }
                }
                if (value.IsNull && name == type.LabelAttribute && !inst.Block.IsExpanded)
                {
                    string label = _l.Options.LabelDefaults?.GetValueOrDefault(inst.Result.Address) ?? inst.Block.Label;
                    inst.Result.LabelDefault = label == inst.Block.Label ? null : label;
                    value = Value.From(label);
                }
                if (value.IsNull)
                {
                    value = spec.ResolveDefault(a => EvalAttr(inst, a), _l.Schema(inst.Result.Connection));
                }
                inst.Memo[name] = value;
                return value;
            }
            finally
            {
                _l.Frames.Pop();
                inst.InProgress.Remove(name);
                // Deferred attributes (memberships, manager) are applied after every object exists.
                var addresses = ConfigLoader.Addresses(frame.Deps).Where(d => d != inst.Result.Address).ToList();
                if (!spec.Deferred)
                {
                    inst.Result.DependsOn.UnionWith(addresses);
                }
                inst.Result.References.UnionWith(addresses);
                if (frame.Deps.Contains(ConfigLoader.SensitiveMarker))
                {
                    inst.Result.SensitiveAttributes.Add(name);
                }
            }
        }

        private Value InstanceObject(ResourceInst inst)
        {
            var fields = inst.Block.Type.Schema.Select(s => new KeyValuePair<string, Value>(s.Name, EvalAttr(inst, s.Name))).ToList();
            fields.Add(new KeyValuePair<string, Value>("dn", EvalAttr(inst, "dn")));
            return new ObjectValue(fields);
        }

        private Value EvalData(DataInst inst)
        {
            if (inst.Result != null)
            {
                _l.Replay(inst.Deps);
                return inst.Result;
            }
            var block = (DataBlock)inst.Owner;
            if (inst.Evaluating)
            {
                throw new DeclaradException($"{block.Describe} refers to itself.", block.Block.Pos);
            }
            inst.Evaluating = true;
            try
            {
                var value = _l.Collect(() =>
                {
                    var args = new Dictionary<string, Value>(StringComparer.Ordinal);
                    foreach (var spec in block.Type.Arguments)
                    {
                        var attr = block.Block.Body.GetAttribute(spec.Name);
                        var v = attr != null ? V.Coerce(Eval(attr.Expr), spec, attr.Pos) : Value.Null;
                        if (v.IsNull)
                        {
                            v = spec.Default ?? Value.Null;
                        }
                        if (v.IsNull && spec.Required)
                        {
                            throw new DeclaradException($"{block.Describe}: argument '{spec.Name}' is required.", block.Block.Pos);
                        }
                        if (!v.IsNull && !v.IsUnknown && spec.AllowedValues != null && !spec.AllowedValues.Contains(V.Str(v)))
                        {
                            throw new DeclaradException($"{block.Describe}: {spec.Name} must be one of {string.Join(", ", spec.AllowedValues)}.", attr?.Pos ?? block.Block.Pos);
                        }
                        args[spec.Name] = v;
                    }
                    if (args.Values.Any(v => v.ContainsUnknown()))
                    {
                        return Value.Unknown;
                    }
                    var directory = _l.Directory(ConnectionOf(block.Block));
                    if (directory == null)
                    {
                        return Value.Unknown;
                    }
                    try
                    {
                        return block.Type.Read(directory, n => args.GetValueOrDefault(n, Value.Null));
                    }
                    catch (DeclaradException ex)
                    {
                        throw new DeclaradException(ex.Message.StartsWith("data.", StringComparison.Ordinal) ? ex.Message : $"{block.Describe}: {ex.Message}", block.Block.Pos);
                    }
                }, out var deps, inst);
                deps.UnionWith(block.ExpansionDeps);
                _l.Replay(block.ExpansionDeps);
                inst.Result = value;
                inst.Deps = deps;
                return value;
            }
            finally
            {
                inst.Evaluating = false;
            }
        }

        // ---- Reference resolution ---------------------------------------------------------------------

        public (Value Value, int Consumed) Resolve(ScopeTraversalExpr reference, Func<Expr, Value> evaluate)
        {
            var steps = reference.Traversal;
            string? NameAt(int i) => i < steps.Count && steps[i] is GetAttr g ? g.Name : null;
            string Required(int i, string usage) => NameAt(i) ?? throw new DeclaradException($"Expected {usage}.", reference.Pos);

            switch (reference.Root)
            {
                case "var":
                    return (ResolveVariable(Required(0, "var.<name>"), reference.Pos), 1);
                case "local":
                    return (ResolveLocal(Required(0, "local.<name>"), reference.Pos), 1);
                case "each":
                {
                    var inst = _l.CurrentInstance;
                    if (inst == null || inst.Owner.Scope != this || inst.Owner.ForEach == null)
                    {
                        throw new DeclaradException("'each' can only be used inside a block that has for_each.", reference.Pos);
                    }
                    return Required(0, "each.key or each.value") switch
                    {
                        "key" => (inst.EachKey, 1),
                        "value" => (inst.EachValue, 1),
                        _ => throw new DeclaradException("Use each.key or each.value.", reference.Pos),
                    };
                }
                case "count":
                {
                    var inst = _l.CurrentInstance;
                    if (inst == null || inst.Owner.Scope != this || inst.Owner.Count == null)
                    {
                        throw new DeclaradException("'count' can only be used inside a block that has count.", reference.Pos);
                    }
                    return Required(0, "count.index") == "index" ? (inst.Key!, 1) : throw new DeclaradException("Use count.index.", reference.Pos);
                }
                case "connection":
                {
                    string local = Required(0, "connection.<name>.<attribute>");
                    if (!ConnectionMap.TryGetValue(local, out var rootName) || !_l.Connections.TryGetValue(rootName, out var c))
                    {
                        throw new DeclaradException($"Connection '{local}' is not available here. Known: {string.Join(", ", ConnectionMap.Keys)}.", reference.Pos);
                    }
                    return (new ObjectValue(new Dictionary<string, Value>
                    {
                        ["name"] = Value.From(rootName),
                        ["domain"] = Value.From(c.Domain),
                        ["base_dn"] = Value.From(c.BaseDn),
                        ["forest_base_dn"] = Value.From(c.Schema.ForestBaseDn),
                        ["server"] = Value.From(c.Server),
                    }), 1);
                }
                case "path":
                    return Required(0, "path.module, path.root or path.cwd") switch
                    {
                        "module" => (Value.From(Dir), 1),
                        "root" => (Value.From(_l.RootDir), 1),
                        "cwd" => (Value.From(Environment.CurrentDirectory), 1),
                        var other => throw new DeclaradException($"Unknown path.{other}; use path.module, path.root or path.cwd.", reference.Pos),
                    };
                case "data":
                {
                    string typeName = Required(0, "data.<type>.<name>");
                    string label = Required(1, "data.<type>.<name>");
                    if (!_data.TryGetValue((typeName, label), out var block))
                    {
                        throw new DeclaradException($"Reference to undeclared data source data.{typeName}.{label}.", reference.Pos);
                    }
                    var instances = Instances(block);
                    if (!block.IsExpanded)
                    {
                        return (EvalData((DataInst)instances[0]), 2);
                    }
                    if (steps.Count > 2 && steps[2] is Hcl.Syntax.Index index)
                    {
                        var key = evaluate(index.Key);
                        return key.IsUnknown || block.UnknownExpansion ? (Value.Unknown, 3) : (EvalData((DataInst)FindInstance(block, instances, key, index.Pos)), 3);
                    }
                    return (Collection(block, instances, i => EvalData((DataInst)i)), 2);
                }
                case "module":
                {
                    string label = Required(0, "module.<name>.<output>");
                    if (!_modules.TryGetValue(label, out var call))
                    {
                        throw new DeclaradException($"Reference to undeclared module '{label}'{ConfigLoader.Suggest(label, _modules.Keys)}.", reference.Pos);
                    }
                    var instances = Instances(call);
                    int consumed = 1;
                    ModuleInst? inst = null;
                    if (!call.IsExpanded)
                    {
                        inst = (ModuleInst)instances[0];
                    }
                    else if (steps.Count > 1 && steps[1] is Hcl.Syntax.Index index)
                    {
                        var key = evaluate(index.Key);
                        if (key.IsUnknown || call.UnknownExpansion)
                        {
                            return (Value.Unknown, 2);
                        }
                        inst = (ModuleInst)FindInstance(call, instances, key, index.Pos);
                        consumed = 2;
                    }
                    if (inst == null)
                    {
                        return (Collection(call, instances, i => ModuleOutputs((ModuleInst)i, reference.Pos)), consumed);
                    }
                    if (NameAt(consumed) is { } output)
                    {
                        return (inst.Child.ResolveOutput(output, reference.Pos), consumed + 1);
                    }
                    return (ModuleOutputs(inst, reference.Pos), consumed);
                }
            }

            var type = ResourceRegistry.Find(reference.Root)
                ?? throw new DeclaradException($"Unknown reference '{reference}'. References start with var., local., each., count., path., data., module. or a resource type ({string.Join(", ", ResourceRegistry.All.Select(t => t.Name))}).", reference.Pos);
            string name = Required(0, $"{type.Name}.<name>");
            if (!_resources.TryGetValue((type.Name, name), out var resource))
            {
                throw new DeclaradException($"Reference to undeclared resource {type.Name}.{name}.", reference.Pos);
            }

            var all = Instances(resource);
            int used = 1;
            ResourceInst? target = null;
            if (!resource.IsExpanded)
            {
                target = (ResourceInst)all[0];
            }
            else if (steps.Count > 1 && steps[1] is Hcl.Syntax.Index idx)
            {
                var key = evaluate(idx.Key);
                if (key.IsUnknown || resource.UnknownExpansion)
                {
                    return (Value.Unknown, 2);
                }
                target = (ResourceInst)FindInstance(resource, all, key, idx.Pos);
                used = 2;
            }

            if (target == null)
            {
                foreach (var i in all.Cast<ResourceInst>())
                {
                    _l.RecordDependency(i.Result.Address);
                }
                return (Collection(resource, all, i => InstanceObject((ResourceInst)i)), used);
            }

            _l.RecordDependency(target.Result.Address);
            if (NameAt(used) is { } attribute)
            {
                return (EvalAttr(target, attribute), used + 1);
            }
            return (InstanceObject(target), used);
        }
    }
}
