using UnrealBuildTool;
public class TestA : ModuleRules
{
	public TestA(ReadOnlyTargetRules Target) : base(Target)
	{	
		PublicIncludePaths.Add("Runtime/TestB/Public");
		PublicIncludePaths.Add("Runtime/TestC/Public");
		PrivateDependencyModuleNames.Add("TestB");
		PrivateDependencyModuleNames.Add("TestC");
	}
}