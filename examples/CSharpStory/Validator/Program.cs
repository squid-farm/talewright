using CSharpStory;
using Talewright.Talemark.Compilation;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: dotnet Story.dll <story-directory>");
    return 2;
}

return StoryProjectValidator.Validate(args[0], StoryExtensions.Create(), Console.Out);
