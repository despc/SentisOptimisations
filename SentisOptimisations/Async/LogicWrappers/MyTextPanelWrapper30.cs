using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using VRage.Game.Entity;

namespace SentisOptimisationsPlugin.Async;

[HarmonyPatch]
public class MyTextPanelWrapper30 : UpdateEntityWrapper
{

    private static FieldInfo _block = typeof(MyMultiTextPanelComponent).GetField("m_block",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    
    public static FieldInfo _multiPanel = typeof(MyFunctionalBlock).GetField("m_multiPanel",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    
    private static FieldInfo _texturesReleased = typeof(MyMultiTextPanelComponent).GetField("m_texturesReleased",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    
    private static FieldInfo _wasInRange = typeof(MyMultiTextPanelComponent).GetField("m_wasInRange",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    
    private static FieldInfo _panels = typeof(MyMultiTextPanelComponent).GetField("m_panels",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    
    private static MethodInfo _IsInRange = typeof(MyMultiTextPanelComponent).GetMethod("IsInRange",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
    
    private static MethodInfo _ReleaseTextures = typeof(MyMultiTextPanelComponent).GetMethod("ReleaseTextures",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
    
    public MyTextPanelWrapper30()
    {
        EntityType = typeof(MyFunctionalBlock);
        
        var methodUpdateIsWorking = typeof(MyMultiTextPanelComponent).GetMethod
        (nameof(MyMultiTextPanelComponent.UpdateScreen),
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);

        HarmonyInstance.Patch(methodUpdateIsWorking, prefix: new HarmonyMethod(
            typeof(UpdateEntityWrapper).GetMethod(nameof(Disabled),
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)));

    }


    public override void Update(MyEntity entity)
    {
        var fb = entity as MyFunctionalBlock;
        var textPanelComponent = _multiPanel.GetValue(fb) as MyMultiTextPanelComponent;
        if (textPanelComponent == null)
        {
            return;
        }

        MyTerminalBlock block = (MyTerminalBlock)_block.GetValue(textPanelComponent);
        if (block == null)
        {
            return;
        }
        if (!block.IsFunctional)
        {
            MyAPIGateway.Utilities.InvokeOnGameThread((Action) (() =>
            {
                try
                {
                    _ReleaseTextures.Invoke(textPanelComponent, new Object[] { });
                }
                catch (Exception ex)
                {
                    SentisOptimisationsPlugin.Log.Error(ex, "Update text panel exception (ReleaseTextures)");
                }
            }));
        }
        else
        {
            _texturesReleased.SetValue(textPanelComponent, false);
            bool isInRange = (bool)_IsInRange.Invoke(textPanelComponent, new object[]{});
            if (isInRange)
                _wasInRange.SetValue(textPanelComponent, isInRange);
            List<MyTextPanelComponent> m_panels = (List<MyTextPanelComponent>)_panels.GetValue(textPanelComponent);
            var blockIsWorking = block.IsWorking;
            MyAPIGateway.Utilities.InvokeOnGameThread((Action) (() =>
            {
                try
                {
                    foreach (var panel in m_panels)
                    {
                        panel.UpdateAfterSimulation(blockIsWorking, isInRange);
                    }
                }
                catch (Exception ex)
                {
                    SentisOptimisationsPlugin.Log.Error(ex, "Update text panel exception");
                }
            }));
            
        }
    }
}