#if TOOLS
#define YARNSPINNER_DEBUG // todo remove
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Godot;
using Godot.Collections;
using Google.Protobuf;
using Yarn;
using Yarn.Compiler;
using Yarn.GodotIntegration;
using Yarn.GodotIntegration;
using Yarn.GodotIntegration.Editor;
using Array = Godot.Collections.Array;
using Directory = System.IO.Directory;
using File = Godot.File;
using Path = System.IO.Path;
[Tool]
public class YarnProjectImporter : EditorImportPlugin
{
    public List<string> parseErrorMessages = new List<string>();
    public override string GetImporterName()
    {
        return "yarnproject";
    }

    public override string GetVisibleName()
    {
        return "Yarn Project";
    }
    public override Array GetRecognizedExtensions() =>
        new Array(new[]
        {
            "yarnproject"
        });

    public override string GetSaveExtension() => "yarnproject";
    public override string GetResourceType()
    {
        return "Resource";
    }
    public override int GetPresetCount()
    {
        return 0;
    }

    public override float GetPriority()
    {
        return 1.0f;
    }
    public override int GetImportOrder()
    {
        return 0;
    }

    public override Array GetImportOptions(int preset)
    {
        return new Array();
    }

    private static string Base64Encode(string plainText)
    {
        var plainTextBytes = Encoding.UTF8.GetBytes(plainText);
        return Convert.ToBase64String(plainTextBytes);
    }

    public override int Import(string assetPath, string savePath, Dictionary options,
        Array platformVariants, Array genFiles)
    {
        GD.Print("Hello.");
        return 0;
    }
    public YarnProject GetDestinationProject(string assetPath)
    {

        var myAssetPath = assetPath;
        var destinationProjectPath = _editorUtility.GetAllAssetsOf<YarnProject>("t:YarnProject")
            .FirstOrDefault(importer =>
            {
                // Does this importer depend on this asset? If so,
                // then this is our destination asset.
                string[] dependencies = ResourceLoader.GetDependencies(assetPath);
                var importerDependsOnThisAsset = dependencies.Contains(myAssetPath);

                return importerDependsOnThisAsset;
            })?.ResourcePath;

        if (destinationProjectPath == null)
        {
            return null;
        }

        return ResourceLoader.Load<YarnProject>(destinationProjectPath);
    }
    public int CompileAllScript(YarnProject project, string assetPath, string savePath)
    {
        GD.Print($"Importing yarn {assetPath} to {savePath}");

        project.ResourceName = Path.GetFileNameWithoutExtension(assetPath);

        foreach (var script in project.SourceScripts)
        {
            // todo: add dependencies on scripts
            // string path = AssetDatabase.GetAssetPath(script);
            // if (string.IsNullOrEmpty(path))
            // {
            //     // This is, for some reason, not a valid script we can
            //     // use. Don't add a dependency on it.
            //     continue;
            // }
            // ctx.DependsOnSourceAsset(path);
            
        }

        // Parse declarations 
        var localDeclarationsCompileJob = CompilationJob.CreateFromFiles(assetPath);
        localDeclarationsCompileJob.CompilationType = CompilationJob.Type.DeclarationsOnly;

        var library = new Library();
        GD.PrintErr("TODO: not automatically discovering [YarnCommand] or [YarnFunction] yet.");
        // ActionManager.AddActionsFromAssemblies(AssemblySearchList());
        // ActionManager.RegisterFunctions(library);
        localDeclarationsCompileJob.Library = library;
        //ListOfFunctions = predeterminedFunctions().ToArray();

        IEnumerable<Declaration> localDeclarations;

        project.CompileErrors.Clear();

        var result = Compiler.Compile(localDeclarationsCompileJob);
        localDeclarations = result.Declarations;

        IEnumerable<Diagnostic> errors;

        errors = result.Diagnostics.Where(d => d.Severity == Diagnostic.DiagnosticSeverity.Error);

        if (errors.Count() > 0)
        {
            // We encountered errors while parsing for declarations.
            // Report them and exit.
            foreach (var error in errors)
            {
                GD.PushError($"Error in Yarn Project: {error}");
                project.CompileErrors.Add($"Error in Yarn Project {assetPath}: {error}");
            }

            return (int)Error.Failed;
        }

        localDeclarations = localDeclarations
            .Where(decl => decl.Name.StartsWith("$Yarn.Internal") == false);

        // Store these so that we can continue displaying them after
        // this import step, in case there are compile errors later.
        // We'll replace this with a more complete list later if
        // compilation succeeds.
        project.SerializedDeclarations = localDeclarations
            .Where(decl => !(decl.Type is FunctionType))
            .Select(decl => new YarnProject.SerializedDeclaration(decl)).ToList();

        // We're done processing this file - we've parsed it, and
        // pulled any information out of it that we need to. Now to
        // compile the scripts associated with this project.

        // First step: check to see if there's any parse errors in the
        // files.
        if (project.ParseErrorMessages.Count != 0)
        {
            // Parse errors! We can't continue.
            string failingScriptNameList = string.Join("\n", project.ScriptsWithParseErrors.Select(script => script.ResourcePath));
            project.CompileErrors.Add($"Parse errors exist in the following files:\n{failingScriptNameList}");
            return (int)Error.Failed;
        }

        // We now now compile!
        var scriptAbsolutePaths = project.SourceScripts.ToList()
            .ConvertAll(scriptResource => ProjectSettings.GlobalizePath(scriptResource.ResourcePath));
        var job = CompilationJob.CreateFromFiles(scriptAbsolutePaths);
        job.VariableDeclarations = localDeclarations;

        job.Library = library;

        CompilationResult compilationResult;

        compilationResult = Compiler.Compile(job);

        errors = compilationResult.Diagnostics.Where(d => d.Severity == Diagnostic.DiagnosticSeverity.Error);

        if (errors.Count() > 0)
        {
            var errorGroups = errors.GroupBy(e => e.FileName);
            foreach (var errorGroup in errorGroups)
            {
                var errorMessages = errorGroup.Select(e => e.ToString());

                foreach (var message in errorMessages)
                {
                    GD.PushError($"Error compiling: {message}");
                }

                // Associate this compile error to the corresponding
                // script's importer.
                project.CompileErrors.AddRange(errorMessages);
                parseErrorMessages.AddRange(errorMessages);
            }

            return (int)Error.Failed;
        }

        if (compilationResult.Program == null)
        {
            GD.PushError("public error: Failed to compile: resulting program was null, but compiler did not report errors.");
            return(int)Error.Failed;
        }

        // Store _all_ declarations - both the ones in this
        // .yarnproject file, and the ones inside the .yarn files.

        // While we're here, filter out any declarations that begin with our
        // Yarn public prefix. These are synthesized variables that are
        // generated as a result of the compilation, and are not declared by
        // the user.
        project.SerializedDeclarations = localDeclarations
            .Concat(compilationResult.Declarations)
            .Where(decl => !decl.Name.StartsWith("$Yarn.Internal."))
            .Where(decl => !(decl.Type is FunctionType))
            .Select(decl => new YarnProject.SerializedDeclaration(decl)).ToList();

        // Clear error messages from all scripts - they've all passed
        // compilation
        project.ParseErrorMessages.Clear();

        CreateYarnInternalLocalizationAssets( project, compilationResult);
        project.localizationType = LocalizationType.YarnInternal;

        // Store the compiled program
        byte[] compiledBytes = null;

        using (var memoryStream = new MemoryStream())
        using (var outputStream = new CodedOutputStream(memoryStream))
        {
            // Serialize the compiled program to memory
            compilationResult.Program.WriteTo(outputStream);
            outputStream.Flush();

            compiledBytes = memoryStream.ToArray();
        }

        project.CompiledYarnProgram = compiledBytes;

        //project.searchAssembliesForActions = AssemblySearchList();

        ResourceSaver.Save(project.ResourcePath, project, ResourceSaver.SaverFlags.ReplaceSubresourcePaths);
        return (int)Error.Ok;
    }

    private Error _Import_Files_Direct(string name, Array<string> files, string savePath, Dictionary options)
    {
        var fileInfo = new List<FileInfo>();
        foreach (var fileName in files)
        {
            fileInfo.Add(new FileInfo(fileName));

        }
        CompilationResult compiledResults = CompileProgram(fileInfo.ToArray());
        foreach (var diagnostic in compiledResults.Diagnostics)
        {
            LogDiagnostic(diagnostic);
        }

        if (compiledResults.Diagnostics.Any(d => d.Severity == Diagnostic.DiagnosticSeverity.Error))
        {
            GD.PrintErr("Not compiling files because errors were encountered.");
            return Error.CompilationFailed;
        }

        string yarnC = null;
        using (var outStream = new MemoryStream())
        using (var codedStream = new CodedOutputStream(outStream))
        {
            compiledResults.Program.WriteTo(codedStream);
            codedStream.Flush();
            yarnC = Convert.ToBase64String(outStream.ToArray());
        }

        var stringTable = new Godot.Collections.Dictionary<string, StringInfo>();
        foreach (var item in compiledResults.StringTable)
        {
            stringTable.Add(item.Key, StringInfo.fromStringInfo(item.Value));
        }

        var resource = new CompiledYarnProject(
            yarnC,
            stringTable
        );

        ResourceSaver.SaverFlags flags = 0;
        if (options.Contains("compress") && options["compress"].ToString() == true.ToString())
        {
            flags = ResourceSaver.SaverFlags.Compress;
        }
        return ResourceSaver.Save($"{savePath}.{GetSaveExtension()}", resource, flags);
    }

    private static void LogDiagnostic(Diagnostic diagnostic)
    {
        var messagePrefix = string.IsNullOrEmpty(diagnostic.FileName) ? string.Empty : $"{diagnostic.FileName}: {diagnostic.Range.Start}:{diagnostic.Range.Start.Character} ";

        var message = messagePrefix + diagnostic.Message;

        switch (diagnostic.Severity)
        {
            case Diagnostic.DiagnosticSeverity.Error:
                GD.PrintErr(message);
                break;
            case Diagnostic.DiagnosticSeverity.Warning:
                GD.Print(message);
                break;
            case Diagnostic.DiagnosticSeverity.Info:
                GD.Print(message);
                break;
        }
    }

    public static CompilationResult CompileProgram(FileInfo[] inputs)
    {
        // The list of all files and their associated compiled results
        var results = new List<(FileInfo file, Program program, IDictionary<string, StringInfo> stringTable)>();

        var compilationJob = CompilationJob.CreateFromFiles(inputs.Select(fileInfo => fileInfo.FullName));

        CompilationResult compilationResult;

        try
        {
            compilationResult = Compiler.Compile(compilationJob);
        }
        catch (Exception e)
        {
            var errorBuilder = new StringBuilder();

            errorBuilder.AppendLine("Failed to compile because of the following error:");
            errorBuilder.AppendLine(e.ToString());

            GD.PrintErr(errorBuilder.ToString());
            throw new Exception();
        }

        return compilationResult;
    }
   
    /// <summary>
    /// If <see langword="true"/>, <see cref="ActionManager"/> will search
    /// all assemblies that have been defined using an <see
    /// cref="AssemblyDefinitionAsset"/> for commands and actions, when this
    /// project is loaded into a <see cref="DialogueRunner"/>. Otherwise,
    /// <see cref="assembliesToSearch"/> will be used.
    /// </summary>
    /// <seealso cref="assembliesToSearch"/>
    public bool searchAllAssembliesForActions = true;
    private Localization developmentLocalization;
    private YarnEditorUtility _editorUtility = new YarnEditorUtility();

    private void CreateYarnInternalLocalizationAssets(YarnProject project, CompilationResult compilationResult)
    {
        // Will we need to create a default localization? This variable
        // will be set to false if any of the languages we've
        // configured in languagesToSourceAssets is the default
        // language.
        var shouldAddDefaultLocalization = true;

        foreach (var pair in project.languagesToSourceAssets)
        {
            // Don't create a localization if the language ID was not
            // provided
            if (string.IsNullOrEmpty(pair.languageID))
            {
                GD.PrintErr($"Not creating a localization for {project.ResourceName} because the language ID wasn't provided. Add the language ID to the localization in the Yarn Project's inspector.");
                continue;
            }

            IEnumerable<StringTableEntry> stringTable;

            // Where do we get our strings from? If it's the default
            // language, we'll pull it from the scripts. If it's from
            // any other source, we'll pull it from the CSVs.
            if (pair.languageID == project.defaultLanguage)
            {
                // We'll use the program-supplied string table.
                stringTable = GenerateStringsTable(project);

                // We don't need to add a default localization.
                shouldAddDefaultLocalization = false;
            }
            else
            {
                try
                {
                    if (pair.stringsFile == null)
                    {
                        // We can't create this localization because we
                        // don't have any data for it.
                        GD.PrintErr($"Not creating a localization for {pair.languageID} in the Yarn Project {project.ResourceName} because a text asset containing the strings wasn't found. Add a .csv file containing the translated lines to the Yarn Project's inspector.");
                        continue;
                    }

                    var csvText = System.IO.File.ReadAllText(ProjectSettings.GlobalizePath(pair.stringsFile.ResourcePath));
                    
                    stringTable = StringTableEntry.ParseFromCSV(csvText);
                }
                catch (ArgumentException e)
                {
                    GD.PrintErr($"Not creating a localization for {pair.languageID} in the Yarn Project {project.ResourceName} because an error was encountered during text parsing: {e}");
                    continue;
                }
            }

            var newLocalization = new Localization();
            newLocalization.LocaleCode = pair.languageID;

            // Add these new lines to the localisation's asset
            foreach (var entry in stringTable)
            {
                newLocalization.AddLocalisedStringToAsset(entry.ID, entry.Text);
            }


            project.localizations.Add(newLocalization);
            newLocalization.ResourceName = pair.languageID;
//             TODO: localizable resources
//             if (pair.assetsFolder != null)
//             {
//                 var assetsFolderPath = AssetDatabase.GetAssetPath(pair.assetsFolder);
//
//                 if (assetsFolderPath == null)
//                 {
//                     // This was somehow not a valid reference?
//                     GD.PrintErr($"Can't find assets for localization {pair.languageID} in {project.name} because a path for the provided assets folder couldn't be found.");
//                 }
//                 else
//                 {
//                     newLocalization.ContainsLocalizedAssets = true;
//
//                     // We need to find the assets used by this
//                     // localization now, and assign them to the
//                     // Localization object.
// #if YARNSPINNER_DEBUG
//                             // This can take some time, so we'll measure
//                             // how long it takes.
//                             var stopwatch = System.Diagnostics.Stopwatch.StartNew();
// #endif
//
//                     // Get the line IDs.
//                     IEnumerable<string> lineIDs = stringTable.Select(s => s.ID);
//
//                     // Map each line ID to its asset path.
//                     var stringIDsToAssetPaths = YarnProjectUtility.FindAssetPathsForLineIDs(lineIDs, assetsFolderPath);
//
//                     // Load the asset, so we can assign the reference.
//                     var assetPaths = stringIDsToAssetPaths
//                         .Select(a => new KeyValuePair<string, Resource>(a.Key, ResourceLoader.Load(a.Value)));
//
//                     newLocalization.AddLocalizedObjects(assetPaths);
//
// #if YARNSPINNER_DEBUG
//                             stopwatch.Stop();
//                             GD.Print($"Imported {stringIDsToAssetPaths.Count()} assets for {project.ResourceName} \"{pair.languageID}\" in {stopwatch.ElapsedMilliseconds}ms");
// #endif
//
//                 }
//             }

            // TODO: save localization file sub-resource
            // ctx.AddObjectToAsset("localization-" + pair.languageID, newLocalization);

            if (pair.languageID == project.defaultLanguage)
            {
                // If this is our default language, set it as such
                project.baseLocalization = newLocalization;

                // Since this is the default language, also populate the line metadata.
                project.lineMetadata = new LineMetadata(LineMetadataTableEntriesFromCompilationResult(compilationResult));
            }
            else
            {
                // This localization depends upon a source asset. Make
                // this asset get re-imported if this source asset was
                // modified
                // todo: set dependency on strings file
                //ctx.DependsOnSourceAsset(AssetDatabase.GetAssetPath(pair.stringsFile));
            }
        }

        if (shouldAddDefaultLocalization)
        {
            // We didn't add a localization for the default language.
            // Create one for it now.
            var stringTableEntries = GetStringTableEntries(project, compilationResult);

            developmentLocalization = new Localization();
            developmentLocalization.ResourceName = $"Default ({project.defaultLanguage})";
            developmentLocalization.LocaleCode = project.defaultLanguage;


            // Add these new lines to the development localisation's asset
            foreach (var entry in stringTableEntries)
            {
                developmentLocalization.AddLocalisedStringToAsset(entry.ID, entry.Text);
            }

            project.baseLocalization = developmentLocalization;
            project.localizations.Add(project.baseLocalization);
        
            // Since this is the default language, also populate the line metadata.
            project.lineMetadata = new LineMetadata(LineMetadataTableEntriesFromCompilationResult(compilationResult));
        }
    }
    
    /// <summary>
    /// Generates a collection of <see cref="StringTableEntry"/>
    /// objects, one for each line in this Yarn Project's scripts.
    /// </summary>
    /// <returns>An IEnumerable containing a <see
    /// cref="StringTableEntry"/> for each of the lines in the Yarn
    /// Project, or <see langword="null"/> if the Yarn Project contains
    /// errors.</returns>
    public IEnumerable<StringTableEntry> GenerateStringsTable(YarnProject project)
    {
        CompilationResult? compilationResult = CompileStringsOnly(project);

        if (!compilationResult.HasValue)
        {
            // We only get no value if we have no scripts to work with.
            // In this case, return an empty collection - there's no
            // error, but there's no content either.
            return new List<StringTableEntry>();
        }

        var errors = compilationResult.Value.Diagnostics.Where(d => d.Severity == Diagnostic.DiagnosticSeverity.Error);

        if (errors.Count() > 0)
        {
            GD.PrintErr($"Can't generate a strings table from a Yarn Project that contains compile errors", null);
            return null;
        }

        return GetStringTableEntries(project, compilationResult.Value);
    }

    private CompilationResult? CompileStringsOnly(YarnProject project)
    {
        var scriptPaths = project.SourceScripts.Where(s => s != null).Select(s => ProjectSettings.GlobalizePath(s.ResourcePath));

        if (scriptPaths.Count() == 0)
        {
            // We have no scripts to work with.
            return null;
        }
        
        // We now now compile!
        var job = CompilationJob.CreateFromFiles(scriptPaths);
        job.CompilationType = CompilationJob.Type.StringsOnly;

        return Compiler.Compile(job);
    }

    private IEnumerable<LineMetadataTableEntry> LineMetadataTableEntriesFromCompilationResult(CompilationResult result)
    {
        return result.StringTable.Select(x => new LineMetadataTableEntry
        {
            ID = x.Key,
            File = x.Value.fileName,
            Node = x.Value.nodeName,
            LineNumber = x.Value.lineNumber.ToString(),
            Metadata = RemoveLineIDFromMetadata(x.Value.metadata).ToArray(),
        }).Where(x => x.Metadata.Length > 0);
    }

    private IEnumerable<StringTableEntry> GetStringTableEntries(YarnProject project, CompilationResult result)
    {
            
        return result.StringTable.Select(x => new StringTableEntry
        {
            ID = x.Key,
            Language = project.defaultLanguage,
            Text = x.Value.text,
            File = x.Value.fileName,
            Node = x.Value.nodeName,
            LineNumber = x.Value.lineNumber.ToString(),
            Lock = YarnImporter.GetHashString(x.Value.text, 8),
            Comment = GenerateCommentWithLineMetadata(x.Value.metadata),
        });
    }
    
    /// <summary>
    /// Generates a string with the line metadata. This string is intended
    /// to be used in the "comment" column of a strings table CSV. Because
    /// of this, it will ignore the line ID if it exists (which is also
    /// part of the line metadata).
    /// </summary>
    /// <param name="metadata">The metadata from a given line.</param>
    /// <returns>A string prefixed with "Line metadata: ", followed by each
    /// piece of metadata separated by whitespace. If no metadata exists or
    /// only the line ID is part of the metadata, returns an empty string
    /// instead.</returns>
    private string GenerateCommentWithLineMetadata(string[] metadata)
    {
        var cleanedMetadata = RemoveLineIDFromMetadata(metadata);

        if (cleanedMetadata.Count() == 0)
        {
            return string.Empty;
        }

        return $"Line metadata: {string.Join(" ", cleanedMetadata)}";
    }


    /// <summary>
    /// Removes any line ID entry from an array of line metadata.
    /// Line metadata will always contain a line ID entry if it's set. For
    /// example, if a line contains "#line:1eaf1e55", its line metadata
    /// will always have an entry with "line:1eaf1e55".
    /// </summary>
    /// <param name="metadata">The array with line metadata.</param>
    /// <returns>An IEnumerable with any line ID entries removed.</returns>
    private IEnumerable<string> RemoveLineIDFromMetadata(string[] metadata)
    {
        return metadata.Where(x => !x.StartsWith("line:"));
    }
    // TODO: search assemblies for annotated methods
    // private List<string> AssemblySearchList()
    // {
    //     // Get the list of assembly names we want to search for actions in.
    //     IEnumerable<AssemblyDefinitionAsset> assembliesToSearch = this.assembliesToSearch;
    //
    //     if (searchAllAssembliesForActions)
    //     {
    //         // We're searching all assemblies for actions. Find all assembly
    //         // definitions in the project, including in packages, and load
    //         // them.
    //         assembliesToSearch = AssetDatabase
    //             .FindAssets($"t:{nameof(AssemblyDefinitionAsset)}")
    //             .Select(guid => AssetDatabase.GUIDToAssetPath(guid))
    //             .Distinct()
    //             .Select(path => AssetDatabase.LoadAssetAtPath<AssemblyDefinitionAsset>(path));
    //     }
    //
    //     // We won't include any assemblies whose names begin with any of
    //     // these prefixes
    //     var excludedPrefixes = new[]
    //     {
    //         "Unity",
    //     };
    //
    //     // Go through each assembly definition asset, figure out its
    //     // assembly name, and add it to the project's list of assembly names
    //     // to search.
    //     var validAssemblies = new List<string>();
    //     foreach (var reference in assembliesToSearch)
    //     {
    //         if (reference == null)
    //         {
    //             continue;
    //         }
    //         var data = new AssemblyDefinition();
    //         EditorJsonUtility.FromJsonOverwrite(reference.text, data);
    //
    //         if (excludedPrefixes.Any(prefix => data.name.StartsWith(prefix)))
    //         {
    //             continue;
    //         }
    //
    //         validAssemblies.Add(data.name);
    //     }
    //     return validAssemblies;
    // }

    private List<FunctionInfo> predeterminedFunctions()
    {
        var functions = ActionManager.FunctionsInfo();

        List<FunctionInfo> f = new List<FunctionInfo>();
        foreach (var func in functions)
        {
            f.Add(FunctionInfo.CreateFunctionInfoFromMethodGroup(func));
        }
        return f;
    }

    // A data class used for deserialising the JSON AssemblyDefinitionAssets
    // into.
    [Serializable]
    private class AssemblyDefinition
    {
        public string name;
    }


}

// A simple class lets us use a delegate as an IEqualityComparer from
// https://stackoverflow.com/a/4607559
public static class Compare
{
    public static IEqualityComparer<T> By<T>(Func<T, T, bool> comparison)
    {
        return new DelegateComparer<T>(comparison);
    }

    private class DelegateComparer<T> : EqualityComparer<T>
    {
        private readonly Func<T, T, bool> comparison;

        public DelegateComparer(Func<T, T, bool> identitySelector)
        {
            comparison = identitySelector;
        }

        public override bool Equals(T x, T y)
        {
            return comparison(x, y);
        }

        public override int GetHashCode(T obj)
        {
            // Force LINQ to never refer to the hash of an object by
            // returning a constant for all values. This is inefficient
            // because LINQ can't use an public comparator, but we're
            // already looking to use a delegate to do a more
            // fine-grained test anyway, so we want to ensure that it's
            // called.
            return 0;
        }
    }
}

[Serializable]
public class FunctionInfo
{
    public string Name;
    public string ReturnType;
    public string[] Parameters;

    public static FunctionInfo CreateFunctionInfoFromMethodGroup(System.Reflection.MethodInfo method)
    {
        var returnType = $"-> {method.ReturnType.Name}";

        var parameters = method.GetParameters();
        var p = new string[parameters.Count()];
        for (int i = 0; i < parameters.Count(); i++)
        {
            var q = parameters[i].ParameterType;
            p[i] = parameters[i].Name;
        }

        return new FunctionInfo
        {
            Name = method.Name,
            ReturnType = returnType,
            Parameters = p,
        };
    }
}
#endif