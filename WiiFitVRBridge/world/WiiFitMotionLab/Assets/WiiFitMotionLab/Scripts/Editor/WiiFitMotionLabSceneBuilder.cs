#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UdonSharp;
using UdonSharp.Compiler;
using UdonSharpEditor;
using VRC.SDK3.Components;

namespace WiiFitMotionLab.Editor
{
    public static class WiiFitMotionLabSceneBuilder
    {
        private const string ScenePath = "Assets/WiiFitMotionLab/WiiFitMotionLab.unity";

        [MenuItem("Wii Fit Motion Lab/Build v0.1 Test Scene")]
        public static void BuildScene()
        {
            // UdonSharp scripts created outside Unity need the companion program
            // asset that the normal "Create U# Script" menu generates.  Create
            // those assets before adding proxies to the scene, especially on the
            // first headless import of a freshly unpacked project.
            EnsureUdonSharpProgramAssets();

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Material snow = CreateMaterial("Snow", new Color(0.78f, 0.9f, 0.98f));
            Material lane = CreateMaterial("Lane", new Color(0.18f, 0.25f, 0.32f));
            Material ski = CreateMaterial("Ski", new Color(0.12f, 0.65f, 0.95f));
            Material board = CreateMaterial("Snowboard", new Color(0.95f, 0.35f, 0.28f));

            AddLightAndSky();
            GameObject systems = new("Systems");
            BalanceLocomotionController controller = systems.AddUdonSharpComponent<BalanceLocomotionController>();

            Transform spawn = Marker("Spawn", new Vector3(0f, 1f, -8f));
            VRCSceneDescriptor descriptor = systems.AddComponent<VRCSceneDescriptor>();
            descriptor.spawns = new[] { spawn };
            controller.defaultResetPoint = spawn;

            CreateCube("Hub", new Vector3(0f, -0.5f, 0f), new Vector3(70f, 1f, 28f), Vector3.zero, snow, false);
            CreateWalkingLane(controller, lane);
            CreateSlope(controller, "Ski", -22f, BalanceLocomotionController.ModeSki, ski);
            CreateSlope(controller, "Snowboard", 22f, BalanceLocomotionController.ModeSnowboard, board);
            CreateResetPedestal(controller);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            Selection.activeGameObject = systems;
            Debug.Log("Wii Fit Motion Lab v0.1 scene created: " + ScenePath);
        }

        public static void ValidateProject()
        {
            EnsureUdonSharpProgramAssets();
            ValidateProgramAsset("Assets/WiiFitMotionLab/Scripts/BalanceLocomotionController.asset");
            ValidateProgramAsset("Assets/WiiFitMotionLab/Scripts/CourseZone.asset");
            ValidateProgramAsset("Assets/WiiFitMotionLab/Scripts/CourseReset.asset");

            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null)
            {
                throw new System.InvalidOperationException("Generated scene not found: " + ScenePath);
            }

            Debug.Log("Wii Fit Motion Lab validation passed: C# + UdonSharp + generated scene");
        }

        private static void ValidateProgramAsset(string path)
        {
            UdonSharpProgramAsset program = AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(path);
            if (program == null ||
                program.CompiledVersion != UdonSharpProgramVersion.CurrentVersion ||
                program.GetSerializedUdonProgramAsset() == null)
            {
                throw new System.InvalidOperationException("UdonSharp program was not compiled: " + path);
            }
        }

        private static void EnsureUdonSharpProgramAssets()
        {
            CreateProgramAsset("Assets/WiiFitMotionLab/Scripts/BalanceLocomotionController.cs");
            CreateProgramAsset("Assets/WiiFitMotionLab/Scripts/CourseZone.cs");
            CreateProgramAsset("Assets/WiiFitMotionLab/Scripts/CourseReset.cs");
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            UdonSharpCompilerV1.CompileSync();
            AssetDatabase.SaveAssets();
        }

        private static void CreateProgramAsset(string scriptPath)
        {
            string assetPath = System.IO.Path.ChangeExtension(scriptPath, ".asset").Replace('\\', '/');
            if (AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(assetPath) != null)
            {
                return;
            }

            MonoScript source = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
            if (source == null)
            {
                throw new System.InvalidOperationException("UdonSharp source not found: " + scriptPath);
            }

            UdonSharpProgramAsset program = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
            program.sourceCsScript = source;
            AssetDatabase.CreateAsset(program, assetPath);
        }

        private static void CreateWalkingLane(BalanceLocomotionController controller, Material material)
        {
            CreateCube("Walking Lane", new Vector3(0f, 0.03f, 31f), new Vector3(5f, 0.12f, 48f), Vector3.zero, material, false);
            Transform reset = Marker("Walking Reset", new Vector3(0f, 1f, 8f));
            CreateCourseVolume("Walking Zone", new Vector3(0f, 4f, 31f), new Vector3(8f, 8f, 50f), controller,
                BalanceLocomotionController.ModeWalking, reset);
            Label("WALK / F8", new Vector3(0f, 2.2f, 7f), Color.white);
        }

        private static void CreateSlope(BalanceLocomotionController controller, string name, float x, int mode, Material accent)
        {
            Vector3 rotation = new(10f, 0f, 0f);
            CreateCube(name + " Snow", new Vector3(x, 9.8f, 39f), new Vector3(15f, 0.8f, 64f), rotation,
                CreateMaterial(name + " Snow Surface", new Color(0.84f, 0.94f, 1f)), false);
            CreateCube(name + " Left Rail", new Vector3(x - 7.5f, 10.2f, 39f), new Vector3(0.35f, 1.2f, 64f), rotation, accent, false);
            CreateCube(name + " Right Rail", new Vector3(x + 7.5f, 10.2f, 39f), new Vector3(0.35f, 1.2f, 64f), rotation, accent, false);
            Transform reset = Marker(name + " Reset", new Vector3(x, 16.1f, 8.5f));
            reset.rotation = Quaternion.Euler(0f, 0f, 0f);
            CreateCourseVolume(name + " Zone", new Vector3(x, 11f, 39f), new Vector3(18f, 20f, 70f), controller, mode, reset);
            Label(name.ToUpperInvariant() + (mode == BalanceLocomotionController.ModeSki ? " / F9" : " / F10"),
                new Vector3(x, 18f, 7f), accent.color);
        }

        private static void CreateCourseVolume(string name, Vector3 position, Vector3 scale,
            BalanceLocomotionController controller, int mode, Transform reset)
        {
            GameObject zone = new(name);
            zone.transform.position = position;
            BoxCollider collider = zone.AddComponent<BoxCollider>();
            collider.isTrigger = true;
            collider.size = scale;
            CourseZone course = zone.AddUdonSharpComponent<CourseZone>();
            course.controller = controller;
            course.movementMode = mode;
            course.resetPoint = reset;
        }

        private static void CreateResetPedestal(BalanceLocomotionController controller)
        {
            GameObject pedestal = CreateCube("RESET", new Vector3(0f, 0.6f, -2f), new Vector3(1.5f, 1.2f, 1.5f),
                Vector3.zero, CreateMaterial("Reset", new Color(1f, 0.78f, 0.15f)), false);
            CourseReset reset = pedestal.AddUdonSharpComponent<CourseReset>();
            reset.controller = controller;
            reset.InteractionText = "コース先頭へ戻る";
        }

        private static GameObject CreateCube(string name, Vector3 position, Vector3 scale, Vector3 rotation,
            Material material, bool trigger)
        {
            GameObject value = GameObject.CreatePrimitive(PrimitiveType.Cube);
            value.name = name;
            value.transform.position = position;
            value.transform.localScale = scale;
            value.transform.rotation = Quaternion.Euler(rotation);
            value.GetComponent<Renderer>().sharedMaterial = material;
            value.GetComponent<BoxCollider>().isTrigger = trigger;
            return value;
        }

        private static Transform Marker(string name, Vector3 position)
        {
            GameObject marker = new(name);
            marker.transform.position = position;
            return marker.transform;
        }

        private static void Label(string text, Vector3 position, Color color)
        {
            GameObject label = new(text);
            label.transform.position = position;
            TextMesh mesh = label.AddComponent<TextMesh>();
            mesh.text = text;
            mesh.fontSize = 64;
            mesh.characterSize = 0.1f;
            mesh.anchor = TextAnchor.MiddleCenter;
            mesh.color = color;
        }

        private static void AddLightAndSky()
        {
            GameObject lightObject = new("Sun");
            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            lightObject.transform.rotation = Quaternion.Euler(48f, -32f, 0f);
            RenderSettings.ambientLight = new Color(0.5f, 0.58f, 0.68f);
        }

        private static Material CreateMaterial(string name, Color color)
        {
            const string folder = "Assets/WiiFitMotionLab/Generated";
            if (!AssetDatabase.IsValidFolder(folder))
            {
                AssetDatabase.CreateFolder("Assets/WiiFitMotionLab", "Generated");
            }
            string safeName = name.Replace(" ", "_");
            string path = folder + "/" + safeName + ".mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(Shader.Find("Standard"));
                AssetDatabase.CreateAsset(material, path);
            }
            material.name = name;
            material.color = color;
            EditorUtility.SetDirty(material);
            return material;
        }
    }
}
#endif
