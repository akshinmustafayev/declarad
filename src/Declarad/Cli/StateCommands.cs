using Cocona;
using Declarad.Config;
using Declarad.Hcl;
using Declarad.State;
using Declarad.State.Backends;
using Microsoft.Extensions.Logging;

namespace Declarad.Cli
{
    public sealed class StateCommands
    {
        private readonly Commands _commands;
        private readonly ConsoleUi _ui = new();

        public StateCommands(ILoggerFactory loggerFactory)
        {
            _commands = new Commands(loggerFactory);
        }

        private static StateManager Open(GlobalOptions g) => Commands.OpenState(g, ProjectSettings.Read(g.ConfigDir));

        [Command("list", Description = "List managed resources")]
        public int List(GlobalOptions g)
        {
            return _commands.Run(g, () =>
            {
                foreach (var r in Open(g).Load().Resources)
                {
                    _ui.WriteLine($"{r.Address}\t{(r.Connection != StateFile.DefaultConnection ? r.Connection + ": " : "")}{r.Dn}");
                }
                return 0;
            });
        }

        [Command("show", Description = "Show a managed resource as recorded in state")]
        public int Show(GlobalOptions g, [Argument] string address)
        {
            return _commands.Run(g, () =>
            {
                var r = Open(g).Load().Find(address) ?? throw new DeclaradException($"{address} is not in the state.");
                _ui.WriteLine($"# {r.Address}");
                _ui.WriteLine($"connection = \"{r.Connection}\"");
                _ui.WriteLine($"guid       = \"{r.Guid}\"");
                _ui.WriteLine($"dn         = {HclWriter.Quote(r.Dn)}");
                foreach (var (k, v) in r.Attributes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    _ui.WriteLine($"{k} = {v.ToHcl()}");
                }
                return 0;
            });
        }

        [Command("rm", Description = "Stop managing resources (the directory objects are not touched)")]
        public int Remove(GlobalOptions g, [Argument] string[] addresses)
        {
            return _commands.Run(g, () =>
            {
                var manager = Open(g);
                using var stateLock = Commands.LockState(g, manager, "state rm");
                var state = manager.Load();
                foreach (string address in addresses)
                {
                    if (!state.Remove(address))
                    {
                        throw new DeclaradException($"{address} is not in the state.");
                    }
                    _ui.WriteLine($"Removed {address}");
                }
                manager.Save(state);
                return 0;
            });
        }

        [Command("mv", Description = "Rename a resource address in the state (prefer moved blocks for shared configurations)")]
        public int Move(GlobalOptions g, [Argument] string from, [Argument] string to)
        {
            return _commands.Run(g, () =>
            {
                var manager = Open(g);
                using var stateLock = Commands.LockState(g, manager, "state mv");
                var state = manager.Load();
                var resource = state.Find(from) ?? throw new DeclaradException($"{from} is not in the state.");
                if (state.Find(to) != null)
                {
                    throw new DeclaradException($"{to} is already in the state.");
                }
                state.Remove(from);
                state.Upsert(new StateResource
                {
                    Address = to,
                    Type = resource.Type,
                    Connection = resource.Connection,
                    Guid = resource.Guid,
                    Dn = resource.Dn,
                    Attributes = resource.Attributes,
                    PreventDestroy = resource.PreventDestroy,
                });
                manager.Save(state);
                _ui.WriteLine($"Moved {from} to {to}");
                return 0;
            });
        }

        [Command("pull", Description = "Print the current state as JSON")]
        public int Pull(GlobalOptions g)
        {
            return _commands.Run(g, () =>
            {
                Console.Out.Write(StateStore.ToText(Open(g).Load()));
                return 0;
            });
        }

        [Command("push", Description = "Replace the state with a local JSON file")]
        public int Push(GlobalOptions g, [Argument(Description = "State file to upload, or - for stdin")] string path, [Option(Description = "Overwrite even if lineage differs or the remote serial is newer")] bool force = false)
        {
            return _commands.Run(g, () =>
            {
                string text = path == "-" ? Console.In.ReadToEnd()
                    : File.Exists(g.Resolve(path)) ? File.ReadAllText(g.Resolve(path))
                    : throw new DeclaradException($"'{path}' does not exist.");
                var incoming = StateStore.Parse(text, path) ?? throw new DeclaradException($"{path} contains no state.");
                var manager = Open(g);
                using var stateLock = Commands.LockState(g, manager, "state push");
                var current = manager.Backend.Read();
                if (current != null && !force)
                {
                    if (current.Lineage != incoming.Lineage)
                    {
                        throw new DeclaradException($"The pushed state has lineage {incoming.Lineage} but the backend has {current.Lineage}. Use --force to replace it.");
                    }
                    if (current.Serial > incoming.Serial)
                    {
                        throw new DeclaradException($"The backend has a newer serial ({current.Serial} > {incoming.Serial}). Use --force to replace it.");
                    }
                }
                // The pushed state replaces the current one: give it a newer serial so that concurrent runs notice.
                incoming.Serial = Math.Max(incoming.Serial, (current?.Serial ?? 0) + 1);
                manager.Backend.Write(incoming);
                _ui.WriteLine(_ui.Green($"Pushed {incoming.Resources.Count} resources to {manager.Backend.Location}."));
                return 0;
            });
        }
    }
}
