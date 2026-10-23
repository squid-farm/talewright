using Talewright.Core.Identity;
using Talewright.Core.Events;

namespace Talewright.Runtime;

/// <summary>Author-only replay instructions, never ordinary player save history.</summary>
public abstract record AuthorCommand;
public sealed record AuthorChoice(string Key) : AuthorCommand;
public sealed record AuthorEdit(AuthorSetup Values) : AuthorCommand;
public sealed record AuthorJump(SceneId Target) : AuthorCommand;
public sealed record AuthorValueChange(string Field, string Before, string After);
public sealed record AuthorCommandResult(int EventStart, IReadOnlyList<GameEvent> Events,
    IReadOnlyList<AuthorValueChange> Changes);
