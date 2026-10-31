# Functions

Functions follow Terraform's standard library where the names match. `declarad schema` lists them all. Any function given an unknown value (offline validation) returns unknown.

## Strings

| Function | Example | Result |
|---|---|---|
| `lower(s)`, `upper(s)` | `upper("ab")` | `"AB"` |
| `title(s)` | `title("john doe")` | `"John Doe"` |
| `trimspace(s)` | `trimspace("  x ")` | `"x"` |
| `trim(s, chars)` | `trim("--x--", "-")` | `"x"` |
| `trimprefix(s, p)`, `trimsuffix(s, p)` | `trimsuffix("web01$", "$")` | `"web01"` |
| `chomp(s)` | `chomp("x\n")` | `"x"` |
| `strrev(s)` | `strrev("abc")` | `"cba"` |
| `startswith(s, p)`, `endswith(s, p)`, `strcontains(s, sub)` | `endswith("a.corp.local", ".local")` | `true` |
| `split(sep, s)` | `split(",", "a,b")` | `["a", "b"]` |
| `join(sep, list...)` | `join("-", ["a", "b"])` | `"a-b"` |
| `substr(s, offset, length)` | `substr("hello", 1, 3)` | `"ell"` (negative offset counts from the end; length `-1` = rest) |
| `replace(s, search, repl)` | `replace("a-b", "-", "_")` | `"a_b"`; with `/regex/` search, `$1` references groups |
| `regex(pattern, s)` | `regex("[0-9]+", "id42")` | `"42"`; named groups give an object, unnamed groups a list |
| `regexall(pattern, s)` | `regexall("[0-9]+", "a1b22")` | `["1", "22"]` |
| `format(spec, args...)` | `format("WS%03d", 7)` | `"WS007"` (verbs `%s %d %f %v %q %t %x %X %o %b %e %%`, flags `- 0 +`, width, precision) |
| `formatlist(spec, args...)` | `formatlist("u-%s", ["a", "b"])` | `["u-a", "u-b"]` |
| `indent(n, s)` | `indent(2, "a\nb")` | `"a\n  b"` |

## Collections

| Function | Example | Result |
|---|---|---|
| `length(x)` | `length([1, 2])` | `2` (lists, objects, strings) |
| `element(list, i)` | `element(["a", "b"], 3)` | `"b"` (wraps around) |
| `index(list, value)` | `index(["a", "b"], "b")` | `1` |
| `contains(list, value)` | `contains(["a"], "a")` | `true` |
| `concat(lists...)` | `concat([1], [2])` | `[1, 2]` |
| `flatten(list)` | `flatten([[1], [2, [3]]])` | `[1, 2, 3]` |
| `distinct(list)` | `distinct([1, 1, 2])` | `[1, 2]` |
| `compact(list)` | `compact(["a", "", null])` | `["a"]` |
| `reverse(list)`, `sort(list)` | `sort(["b", "a"])` | `["a", "b"]` |
| `slice(list, from, to)` | `slice([1, 2, 3], 1, 3)` | `[2, 3]` |
| `range(n)`, `range(a, b[, step])` | `range(3)` | `[0, 1, 2]` |
| `chunklist(list, size)` | `chunklist([1, 2, 3], 2)` | `[[1, 2], [3]]` |
| `keys(obj)`, `values(obj)` | `keys({ b = 1, a = 2 })` | `["a", "b"]` (sorted by key) |
| `lookup(obj, key[, default])` | `lookup({ a = 1 }, "b", 0)` | `0` |
| `merge(objects...)` | `merge({ a = 1 }, { a = 2 })` | `{ a = 2 }` |
| `zipmap(keys, values)` | `zipmap(["a"], [1])` | `{ a = 1 }` |
| `coalesce(values...)` | `coalesce("", null, "x")` | `"x"` |
| `coalescelist(lists...)` | `coalescelist([], [1])` | `[1]` |
| `one(list)` | `one([])` | `null` (error for more than one element) |
| `alltrue(list)`, `anytrue(list)` | `alltrue([true, false])` | `false` |
| `sum(list)` | `sum([1, 2])` | `3` |
| `toset(list)` | `toset(["b", "a", "b"])` | `["a", "b"]` (strings are sorted) |
| `setunion`, `setintersection`, `setsubtract`, `setproduct` | `setsubtract(["a", "b"], ["a"])` | `["b"]` |
| `transpose(obj)` | `transpose({ a = ["x"] })` | `{ x = ["a"] }` |

## Numbers

`abs`, `ceil`, `floor`, `signum`, `max(n...)`, `min(n...)`, `pow(a, b)`, `log(n, base)`, `parseint("ff", 16)`.

## Types

| Function | Purpose |
|---|---|
| `tostring`, `tonumber`, `tobool`, `tolist`, `tomap` | Conversion (`null` stays `null`). |
| `type(x)` | `"string"`, `"number"`, `"bool"`, `"list"`, `"object"`, `"null"`. |
| `try(expr...)` | First argument that evaluates without error: `try(var.cfg.optional, "default")`. |
| `can(expr)` | Whether the expression evaluates without error. |

## Encoding and hashing

`jsonencode`, `jsondecode`, `csvdecode` (first row is the header; returns a list of objects), `base64encode`, `base64decode`, `urlencode`, `md5`, `sha1`, `sha256`, `sha512`, `base64sha256`.

## Files and environment

| Function | Purpose |
|---|---|
| `env(name[, default])` | Environment variable; error if unset and no default. |
| `file(path)` | File contents. Relative paths are relative to the root module directory; use `"${path.module}/x"` inside modules. |
| `filebase64(path)`, `fileexists(path)` | |
| `fileset(dir, pattern)` | Matching files relative to `dir` (`*`, `**`, `?`). |
| `templatefile(path, vars)` | Render a template file with the given variables. |
| `abspath`, `basename`, `dirname`, `pathexpand` | Path helpers. |

## Directory helpers

| Function | Example | Result |
|---|---|---|
| `dnjoin(type, value, parent)` | `dnjoin("OU", "R&D, Labs", "DC=corp,DC=local")` | `"OU=R&D\, Labs,DC=corp,DC=local"` |
| `dnescape(value)` | `dnescape("Doe, John")` | `"Doe\, John"` |
| `dnparent(dn)` | `dnparent("CN=u,OU=x,DC=y")` | `"OU=x,DC=y"` |
| `dnvalue(dn)` | `dnvalue("CN=Doe\, John,DC=y")` | `"Doe, John"` |
| `domaindn(domain)` | `domaindn("corp.local")` | `"DC=corp,DC=local"` |
| `filterescape(value)` | `filterescape("a*b")` | `"a\2ab"` (for LDAP filters in `data "objects"`) |
