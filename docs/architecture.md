# Architecture

This page is for people changing declarad itself.

## Layout

```
src/Declarad/
  Program.cs        Cocona host, logging
  Cli/              commands, state commands, plan rendering, console UI, schema docs
  Hcl/              the language: lexer, parser, evaluator, functions, type constraints, formatter
    Syntax/         syntax tree: Body, Block, Attribute, expressions, traversers
    Values/         Value and its kinds (null, unknown, string, bool, number, list, object)
  Config/           loading a configuration: scopes, references, for_each/count, connections, settings
    Modules/        module calls and instances, sources, installer, manifest
  Resources/        resource types (schema + LDAP mapping + apply logic)
    Schema/         AttributeSpec, attribute types and LDAP formats, shared specs
    DataSources/    data source types and their registry
    Dns/            dns_zone, dns_record and the dnsRecord codec
  Directory/        IDirectory and its LDAP implementation, DN/SID helpers, security descriptor editing
  Engine/           planner (diff + ordering) and applier (phased execution)
  State/            state model and store
    Backends/       backends (local, http, s3, azurerm), locking, StateManager
  Export/           directory → configuration
tests/Declarad.Tests/
  Support/          InMemoryDirectory (an AD emulation enforcing the rules the engine depends on), test attributes
  *Tests.cs         unit, engine and integration tests
```

Each class, record, interface and enum has its own file, named after the type. The exceptions are small families that only make sense together: the `Expr` records (`Hcl/Syntax/Expressions.cs`), the traversal steps (`Hcl/Syntax/Traversers.cs`) and the template parts (`Hcl/TemplatePart.cs`). Namespaces match the folders (`Declarad.Hcl.Syntax`, `Declarad.Resources.Dns`, ...). Where a type name clashes with a `System` type (`Attribute`, `Index`), files that use it import it with an alias such as `using Index = Declarad.Hcl.Syntax.Index;`.

## A run, end to end

```
declarad apply
  └─ Cli/Commands.ApplyInternal
       ├─ ConfigLoader.Load              parse every .dad file, build module scopes, evaluate
       │    └─ ModuleScope               resolves var/local/each/count/path/data/module/resource references
       ├─ StateManager (backend)         lock, load state
       ├─ Planner.Build                  read objects by GUID, diff, order
       ├─ PlanRenderer                   print, confirm
       └─ Applier.Apply                  phase 1 objects, phase 2 deferred + attachments, phase 3 deletes; save state per step
```

### Hcl

- `Lexer` produces tokens; strings and heredocs become *template parts* (literals, `${}` interpolations, `%{}` directives with strip flags).
- `Parser` is recursive descent with precedence climbing and produces `Body`/`Block`/`Attribute`/`Expr` records (`Hcl/Syntax/`).
- `Evaluator` evaluates expressions. Name resolution is delegated to an `IReferenceResolver` (the module scope); local names of `for` expressions and template `for` loops live in a scope stack. `Value.Unknown` propagates through every operation. `try`/`can` are special forms.
- `Functions` is a table of name → (arity, implementation).
- `TypeConstraint` parses `list(object({...}))` from expressions and converts values.
- `Formatter` re-indents by tracking bracket depth per line and verifies that the token stream is unchanged.

### Config

`ConfigLoader` owns the shared state of one load: errors, warnings, the evaluation frame stack, connections and opened directories. Each module instance is a `ModuleScope`, which registers its blocks and resolves references lazily:

- A **resource attribute** is evaluated on first use and memoized per instance (`EvalAttr`). `ou.x.dn` evaluates only `name` and `path`, which is why mutual references between groups don't cycle.
- **Dependencies** are recorded through a frame stack. Each resource-attribute frame turns the addresses it saw into `DependsOn` (if the attribute is not deferred) and `References` (always). Locals, outputs, module inputs and data sources are *collector* frames: they memoize what they saw and replay it to whoever reads them later.
- **Expansion** (`for_each`/`count`) happens once per block, in an isolated frame.
- **Modules**: `CreateChild` resolves the source (local path, or the installed copy from `.declarad/modules/modules.json`), registers the child's files, checks inputs and builds the child's connection map.

The result is a flat `Configuration`: resource instances with addresses, attributes, DN, connection, dependencies and lifecycle settings, plus outputs, moves, imports and warnings.

### Resources

`ResourceType` describes one object class:

- `Schema`: a list of `AttributeSpec` (type, default, LDAP attribute and format, flags such as `Deferred`, `Additive`, `CreateOnly`, `Immutable`, `Sensitive`, `IsDn`, `AcceptsSamAccountName`, plus validation and normalization).
- `Read(object)`: the directory object as schema values. Simple LDAP mappings are generic; `ReadSpecial` handles the rest (UAC bits, groupType, security descriptors, ...).
- `Create`, `Update` (rename/move, then modifications), `ApplyDeferred`, `Delete`, with hooks (`AddCreateAttributes`, `AfterCreate`, `SpecialModifications`, `AfterUpdate`).

`AttachmentType` is for resources that modify an object they don't own (`group_membership`, `gpo_link`, `dns_record`): `ReadAttachment`, `Attach`, `Detach`, and optionally `CreatesTarget`/`CreateTarget`.

### Engine

`Planner.Build`:
1. Resolves sAMAccountName references to DNs, computes attachment target DNs, and merges `groups` into authoritative group member lists.
2. Maps state through `moved` blocks.
3. For each resource: finds the prior state record, locates the object (by GUID, then by an `import` block, then by DN with `--adopt`), checks its class, and diffs `Read()` against the configuration (`Diff`/`DiffList`).
4. Plans deletes for state entries no longer configured.
5. Orders creates/updates topologically (references + `depends_on` + containment), and deletes attachments first, then deepest DN first.

`Applier.Apply` executes in three phases and calls `saveState` after every step. Before phase 2 it re-reads objects and recomputes list deltas, because renames in phase 1 change member DNs.

### State

`StateFile` is the JSON model. `IStateBackend` implementations read, write and lock it. `StateManager` adds the serial/lineage conflict check and timed locking. Lock information uses Terraform's format.

### Directory

`IDirectory` is the only way the engine touches AD. `LdapDirectory` implements it with System.DirectoryServices.Protocols (paged searches, ranged attribute retrieval, `<GUID=...>`/`<SID=...>` lookups, SD flags control). Tests use `InMemoryDirectory`.

## Adding a resource type

1. Create a class deriving from `ResourceType` (or `AttachmentType`) in `src/Declarad/Resources/`.
2. Define `Name`, `ObjectClass`, `RdnType`, `Description`, `Schema`, and `Filter` / `DefaultSearchBase` if the defaults don't fit.
3. Map simple attributes with `Ldap = "..."` (and `Format` for booleans, integers, durations). Override `ReadSpecial` / `AddCreateAttributes` / `SpecialModifications` / `ApplyDeferred` for anything else.
4. Register it in `ResourceRegistry.All`; its position sets the tie-break order for creation. A data source is created automatically for object types.
5. Teach `InMemoryDirectory` any rules the real AD enforces for the class, and add engine tests: create, converge (plan again → no changes), update, drift, delete.
6. Write `docs/resources/<type>.md` with a `<!-- schema:<type> -->` marker. `DocsTests` checks that the generated table is current. Regenerate it with `declarad schema <type> --markdown`.

## Testing

```sh
dotnet test
```

- **Unit tests** cover the language, DN/SID/security-descriptor codecs, DNS encoding and settings parsing.
- **Engine tests** run the real planner and applier against `InMemoryDirectory`. Always assert convergence: a second plan must be empty.
- **Integration tests** run when their environment variable is set: `DECLARAD_TEST_S3_ENDPOINT` (S3/LocalStack), `DECLARAD_TEST_AZURITE` (Azurite connection string).
- **Docs tests** check that resource pages match the schema and that every example configuration validates and is formatted.
