using Talewright.Core.Identity;

namespace Talewright.Debugger;

public sealed record InspectorSelection(SceneId Scene, string? ChoiceKey = null);
