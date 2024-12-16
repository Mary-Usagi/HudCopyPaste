﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using HUDLayoutHelper.Utilities;
using ImGuiNET;


namespace HUDLayoutHelper.KeyboardShortcuts;

internal class KeybindHandler {
    private List<HudAction> _allActions { get; } = [
            HudActions.Copy,
            HudActions.Paste,
            HudActions.Undo,
            HudActions.Redo,
            HudActions.ToggleAlignmentOverlay
    ];

    private static List<Keybind> _registeredKeybinds = [];

    internal static Keybind? Register(HudAction action, Keybind keybind) {
        // check if keybind with same main key and modifier keys is already registered
        if (_registeredKeybinds.Any(k => k.MainKey == keybind.MainKey &&
            k.ModifierKeys.SequenceEqual(keybind.ModifierKeys))) {
            Plugin.Log.Error($"Keybind {keybind} is already registered.");
            return null;
        }
        _registeredKeybinds.Add(keybind);
        return keybind;
    }
    internal static bool Unregister(Keybind keybind) {
        if (_registeredKeybinds.Contains(keybind)) {
            _registeredKeybinds.Remove(keybind);
            return true;
        }
        Plugin.Log.Warning($"Keybind {keybind} is not registered.");
        return false;
    }


    private HudElementData? currentlyCopied = null;
    private readonly Plugin _plugin;


    public KeybindHandler(Plugin plugin) {
        _plugin = plugin;
    }

    /// <summary>
    /// Handles keyboard shortcuts for copy, paste, undo, and redo actions.
    /// </summary>
    /// <param name="framework">The framework interface.</param>
    internal unsafe void HandleKeyboardShortcuts(IFramework framework) {
        // Executes every frame
        if (!Plugin.ClientState.IsLoggedIn) return;
        if (Plugin.ClientState is not { LocalPlayer.ClassJob.RowId: var classJobId }) return;
        if (ImGui.GetIO().WantCaptureKeyboard) return; // TODO: Necessary? 

        // Get the state of the control key, abort if not pressed 
        KeyStateFlags ctrlKeystate = UIInputData.Instance()->GetKeyState(SeVirtualKey.CONTROL);
        if (!ctrlKeystate.HasFlag(KeyStateFlags.Down)) return;

        // Set the keyboard action based on the key states
        KeybindAction detectedKeybindAction = KeybindAction.None;
        foreach ((var keybindAction, var keybind) in KeybindMap) {
            foreach (var keyCombo in keybind.Combos) {
                KeyStateFlags keyState = UIInputData.Instance()->GetKeyState(keyCombo.MainKey);
                if (keyCombo.ShiftUsed && !UIInputData.Instance()->GetKeyState(SeVirtualKey.SHIFT).HasFlag(KeyStateFlags.Down)) continue;
                if (!keyCombo.ShiftUsed && UIInputData.Instance()->GetKeyState(SeVirtualKey.SHIFT).HasFlag(KeyStateFlags.Down)) continue;
                if (keyState.HasFlag(keyCombo.State)) {
                    detectedKeybindAction = keybindAction;
                    break;
                }
            }
        }

        if (detectedKeybindAction == KeybindAction.None) return;
        Plugin.Debug.Log(Plugin.Log.Debug, $"Keybind.Action: {detectedKeybindAction}");

        // Abort if a popup is open
        if (Plugin.HudLayoutWindow->NumOpenPopups > 0) {
            Plugin.Debug.Log(Plugin.Log.Warning, "Popup open, not executing action.");
            return;
        }

        // Check if the layout editor is open, abort if not
        if (Plugin.AgentHudLayout == null || Plugin.HudLayoutScreen == null) return;

        // Depending on the keyboard action, execute the corresponding operation
        HudElementData? changedElement = null;
        switch (detectedKeybindAction) {
            case KeybindAction.Copy:
                HandleCopyAction();
                break;
            case KeybindAction.Paste:
                changedElement = HandlePasteAction();
                break;
            case KeybindAction.Undo:
                changedElement = HandleUndoAction();
                break;
            case KeybindAction.Redo:
                changedElement = HandleRedoAction();
                break;
            case KeybindAction.ToggleAlignmentOverlay:
                _plugin.ToggleAlignmentOverlay();
                break;
        }

        // Update previousElements if a change was made
        if (changedElement != null) {
            Plugin.Debug.Log(Plugin.Log.Debug, $"Changed Element: {changedElement}");
            HudElementData? changedPreviousElement = null;
            var previousElements = _plugin.PreviousHudLayoutIndexElements[Utils.GetCurrentHudLayoutIndex()];
            previousElements.TryGetValue(changedElement.ElementId, out changedPreviousElement);
            previousElements[changedElement.ElementId] = changedElement;
        }
    }

    /// <summary>
    /// Copy the position of the selected element to the clipboard. 
    /// </summary>
    internal unsafe void HandleCopyAction() {
        // Get the currently selected element, abort if none is selected
        AtkResNode* selectedNode = Utils.GetCollisionNodeByIndex(Plugin.HudLayoutScreen, 0);
        if (selectedNode == null) {
            Plugin.Log.Debug($"No element selected.");
            return;
        }

        // Create a new HudElementData object with the data of the selected element
        HudElementData selectedNodeData = new HudElementData(selectedNode);
        currentlyCopied = selectedNodeData;

        // Copy the data to the clipboard
        ImGui.SetClipboardText(selectedNodeData.ToString());
        Plugin.Debug.Log(Plugin.Log.Debug, $"Copied to Clipboard: {selectedNodeData}");
        Plugin.Log.Debug($"Copied position to clipboard: {selectedNodeData.PrettyPrint()}");
    }

    /// <summary>
    /// Paste the position from the clipboard to the selected element 
    /// and simulate a mouse click on the element.
    /// </summary>
    internal unsafe HudElementData? HandlePasteAction() {
        // Get the currently selected element, abort if none is selected
        AtkResNode* selectedNode = Utils.GetCollisionNodeByIndex(Plugin.HudLayoutScreen, 0);
        if (selectedNode == null) {
            Plugin.Log.Debug($"No element selected.");
            return null;
        }

        // Get the clipboard text
        string clipboardText = ImGui.GetClipboardText();
        if (clipboardText == null) {
            Plugin.Log.Debug($"Clipboard is empty.");
            return null;
        }

        // Parse the clipboard text to a HudElementData object
        HudElementData? parsedData = null;
        try {
            parsedData = JsonSerializer.Deserialize<HudElementData>(clipboardText);
        } catch {
            Plugin.Log.Warning($"Clipboard data could not be parsed: '{clipboardText}'");
            return null;
        }
        if (parsedData == null) {
            Plugin.Log.Warning($"Clipboard data could not be parsed. '{clipboardText}'");
            return null;
        }
        Plugin.Debug.Log(Plugin.Log.Debug, $"Parsed Clipboard: {parsedData}");

        // Save the current state of the selected element for undo operations
        HudElementData previousState = new HudElementData(selectedNode);


        // Set the position of the currently selected element to the parsed position
        selectedNode->ParentNode->SetPositionShort(parsedData.PosX, parsedData.PosY);

        // Add the previous state and the new state to the undo history
        int hudLayoutIndex = Utils.GetCurrentHudLayoutIndex();
        _plugin.HudHistoryManager.AddUndoAction(hudLayoutIndex, previousState, parsedData);

        // Simulate Mouse Click
        Utils.SimulateMouseClickOnHudElement(selectedNode, 0, parsedData, Plugin.HudLayoutScreen, _plugin.CUSTOM_FLAG);

        // Send Event to HudLayout to inform about a change 
        Utils.SendChangeEvent(Plugin.AgentHudLayout);

        Plugin.Log.Debug($"Pasted position to selected element: {previousState.ResNodeDisplayName} ({previousState.PosX}, {previousState.PosY}) -> ({parsedData.PosX}, {parsedData.PosY})");
        return parsedData;
    }

    /// <summary>
    /// Undo the last operation and simulate a mouse click on the element.
    /// </summary>
    internal unsafe HudElementData? HandleUndoAction() {
        // Get the last added action from the undo history
        (HudElementData? oldState, HudElementData? newState) = _plugin.HudHistoryManager.PeekUndoAction(Utils.GetCurrentHudLayoutIndex());
        if (oldState == null || newState == null) {
            Plugin.Log.Debug($"Nothing to undo.");
            return null;
        }

        // Find node with same name as oldState
        (nint undoNodePtr, uint undoNodeId) = Utils.FindHudResnodeByName(Plugin.HudLayoutScreen, oldState.ResNodeDisplayName);
        if (undoNodePtr == nint.Zero) {
            Plugin.Log.Warning($"Could not find node with name '{oldState.ResNodeDisplayName}'");
            return null;
        }
        AtkResNode* undoNode = (AtkResNode*)undoNodePtr;

        HudElementData undoNodeState = new HudElementData(undoNode);

        // Set the position of the currently selected element to the parsed position
        undoNode->ParentNode->SetPositionShort(oldState.PosX, oldState.PosY);

        _plugin.HudHistoryManager.PerformUndo(Utils.GetCurrentHudLayoutIndex(), undoNodeState);

        // Simulate Mouse Click
        Utils.SimulateMouseClickOnHudElement(undoNode, undoNodeId, oldState, Plugin.HudLayoutScreen, _plugin.CUSTOM_FLAG);

        // Send Event to HudLayout to inform about a change 
        Utils.SendChangeEvent(Plugin.AgentHudLayout);

        Plugin.Log.Debug($"Undo: Moved '{undoNodeState.ResNodeDisplayName}' from ({undoNodeState.PosX}, {undoNodeState.PosY}) back to ({oldState.PosX}, {oldState.PosY})");

        return oldState;
    }

    /// <summary>
    /// Redo the last operation and simulate a mouse click on the element.
    /// </summary>
    internal unsafe HudElementData? HandleRedoAction() {
        // Get the last added action from the redo history
        (HudElementData? oldState, HudElementData? newState) = _plugin.HudHistoryManager.PeekRedoAction(Utils.GetCurrentHudLayoutIndex());
        if (oldState == null || newState == null) {
            Plugin.Log.Debug($"Nothing to redo.");
            return null;
        }

        // Find node with same name as new state
        (nint redoNodePtr, uint redoNodeId) = Utils.FindHudResnodeByName(Plugin.HudLayoutScreen, newState.ResNodeDisplayName);
        if (redoNodePtr == nint.Zero) {
            Plugin.Log.Warning($"Could not find node with name '{newState.ResNodeDisplayName}'");
            return null;
        }
        AtkResNode* redoNode = (AtkResNode*)redoNodePtr;
        HudElementData redoNodeState = new HudElementData(redoNode);

        // Set the position of the currently selected element to the parsed position
        redoNode->ParentNode->SetPositionShort(newState.PosX, newState.PosY);

        _plugin.HudHistoryManager.PerformRedo(Utils.GetCurrentHudLayoutIndex(), redoNodeState);

        // Simulate Mouse Click
        Utils.SimulateMouseClickOnHudElement(redoNode, redoNodeId, newState, Plugin.HudLayoutScreen, _plugin.CUSTOM_FLAG);

        // Send Event to HudLayout to inform about a change 
        Utils.SendChangeEvent(Plugin.AgentHudLayout);

        Plugin.Log.Debug($"Redo: Moved '{redoNodeState.ResNodeDisplayName}' again from ({redoNodeState.PosX}, {redoNodeState.PosY}) to ({newState.PosX}, {newState.PosY})");

        return newState;
    }
}
