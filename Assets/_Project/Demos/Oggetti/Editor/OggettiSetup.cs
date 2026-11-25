using CardboardXR.Demos;
using CardboardXR.Demos.Oggetti;
using UnityEditor;
using UnityEngine;

namespace ProjectBootstrap
{
    // Oggetti: app AR per posizionare modelli 3D Sketchfab nella stanza, spostarli, ridimensionali e ruotarli verticalmente.
    class OggettiSetup : DemoSetup
    {
        public override string Name => "Oggetti";
        public override int Order => 56;

        public override DemoCatalog.Entry Entry => new()
        {
            Id = "oggetti",
            Title = "Oggetti",
            Description = "Cerca e posiziona modelli 3D da Sketchfab sulle superfici della stanza, spostali, ridimensionali e ruotali.",
            Tagline = "Posiziona oggetti 3D Sketchfab nella tua stanza",
            Icon = "view_in_ar",
            Tint = new Color(0.18f, 0.56f, 0.54f),
            Requirements = new DemoRequirements { SixDof = Need.On, Passthrough = Need.On, Hands = Need.Off, HandPinch = false },
        };

        public override void BuildScene(string folder)
        {
            var shader = Shader.Find("Universal Render Pipeline/Simple Lit") ?? DemoAssets.Unlit;
            var modelMat = StereoBuilder.CreateMaterial("ModelBase", shader, Color.white, null, 1f, folder);
            modelMat.SetFloat("_Cull", 2f);
            EditorUtility.SetDirty(modelMat);

            var demo = new GameObject("Oggetti Demo").AddComponent<OggettiDemo>();
            var serialized = new SerializedObject(demo);
            serialized.FindProperty("m_ModelBaseMaterial").objectReferenceValue = modelMat;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            var environment = DemoAssets.Environment();
            var grid = StereoBuilder.SaveAsset(DemoAssets.CreateGridTexture(), $"{folder}/Grid.asset");
            const float floorSize = 30f;
            DemoAssets.Floor(environment.transform,
                StereoBuilder.CreateMaterial("Oggetti Floor", DemoAssets.Unlit, Color.white, grid, floorSize / 0.5f, folder), floorSize);
        }
    }
}
