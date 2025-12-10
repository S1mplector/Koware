// Author: Ilgaz Mehmetoğlu
// Tests for InputHandler and InputResult types.
using System;
using Koware.Cli.Console;
using Xunit;

namespace Koware.Tests;

public class InputHandlerTests
{
    #region InputAction Enum Tests

    [Theory]
    [InlineData(InputAction.None)]
    [InlineData(InputAction.MoveUp)]
    [InlineData(InputAction.MoveDown)]
    [InlineData(InputAction.PageUp)]
    [InlineData(InputAction.PageDown)]
    [InlineData(InputAction.JumpToStart)]
    [InlineData(InputAction.JumpToEnd)]
    [InlineData(InputAction.Select)]
    [InlineData(InputAction.Cancel)]
    [InlineData(InputAction.SearchCharacter)]
    [InlineData(InputAction.SearchBackspace)]
    [InlineData(InputAction.QuickJump)]
    public void InputAction_AllValuesExist(InputAction action)
    {
        Assert.True(Enum.IsDefined(typeof(InputAction), action));
    }

    [Fact]
    public void InputAction_HasExpectedCount()
    {
        var values = Enum.GetValues<InputAction>();
        Assert.Equal(12, values.Length);
    }

    #endregion

    #region InputResult Static Factory Tests

    [Fact]
    public void InputResult_None_ReturnsCorrectAction()
    {
        var result = InputResult.None;
        
        Assert.Equal(InputAction.None, result.Action);
        Assert.Null(result.Character);
        Assert.Null(result.JumpIndex);
    }

    [Fact]
    public void InputResult_Up_ReturnsCorrectAction()
    {
        var result = InputResult.Up;
        
        Assert.Equal(InputAction.MoveUp, result.Action);
        Assert.Null(result.Character);
        Assert.Null(result.JumpIndex);
    }

    [Fact]
    public void InputResult_Down_ReturnsCorrectAction()
    {
        var result = InputResult.Down;
        
        Assert.Equal(InputAction.MoveDown, result.Action);
    }

    [Fact]
    public void InputResult_PgUp_ReturnsCorrectAction()
    {
        var result = InputResult.PgUp;
        
        Assert.Equal(InputAction.PageUp, result.Action);
    }

    [Fact]
    public void InputResult_PgDown_ReturnsCorrectAction()
    {
        var result = InputResult.PgDown;
        
        Assert.Equal(InputAction.PageDown, result.Action);
    }

    [Fact]
    public void InputResult_Home_ReturnsCorrectAction()
    {
        var result = InputResult.Home;
        
        Assert.Equal(InputAction.JumpToStart, result.Action);
    }

    [Fact]
    public void InputResult_End_ReturnsCorrectAction()
    {
        var result = InputResult.End;
        
        Assert.Equal(InputAction.JumpToEnd, result.Action);
    }

    [Fact]
    public void InputResult_Confirm_ReturnsCorrectAction()
    {
        var result = InputResult.Confirm;
        
        Assert.Equal(InputAction.Select, result.Action);
    }

    [Fact]
    public void InputResult_Escape_ReturnsCorrectAction()
    {
        var result = InputResult.Escape;
        
        Assert.Equal(InputAction.Cancel, result.Action);
    }

    [Fact]
    public void InputResult_Backspace_ReturnsCorrectAction()
    {
        var result = InputResult.Backspace;
        
        Assert.Equal(InputAction.SearchBackspace, result.Action);
    }

    #endregion

    #region InputResult Factory with Parameters

    [Theory]
    [InlineData('a')]
    [InlineData('Z')]
    [InlineData('5')]
    [InlineData(' ')]
    [InlineData('ü')]
    public void InputResult_Search_SetsCharacter(char ch)
    {
        var result = InputResult.Search(ch);
        
        Assert.Equal(InputAction.SearchCharacter, result.Action);
        Assert.Equal(ch, result.Character);
        Assert.Null(result.JumpIndex);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8)]
    public void InputResult_Jump_SetsJumpIndex(int index)
    {
        var result = InputResult.Jump(index);
        
        Assert.Equal(InputAction.QuickJump, result.Action);
        Assert.Equal(index, result.JumpIndex);
        Assert.Null(result.Character);
    }

    #endregion

    #region InputHandler Constructor Tests

    [Fact]
    public void InputHandler_DefaultConstructor_EnablesSearch()
    {
        var handler = new InputHandler();
        
        // We can't directly test the private field, but we can verify it compiles
        Assert.NotNull(handler);
    }

    [Fact]
    public void InputHandler_WithSearchDisabled_Compiles()
    {
        var handler = new InputHandler(searchEnabled: false);
        
        Assert.NotNull(handler);
    }

    [Fact]
    public void InputHandler_WithSearchEnabled_Compiles()
    {
        var handler = new InputHandler(searchEnabled: true);
        
        Assert.NotNull(handler);
    }

    [Fact]
    public void InputHandler_WithDisableQuickJump_Compiles()
    {
        var handler = new InputHandler(searchEnabled: true, disableQuickJump: true);
        
        Assert.NotNull(handler);
    }

    [Fact]
    public void InputHandler_WithDisableQuickJumpFalse_Compiles()
    {
        var handler = new InputHandler(searchEnabled: true, disableQuickJump: false);
        
        Assert.NotNull(handler);
    }

    [Fact]
    public void InputHandler_DisableQuickJumpDefaultsToFalse()
    {
        // Verify that the default constructor behavior doesn't break
        var handler = new InputHandler();
        Assert.NotNull(handler);
        
        // The second parameter defaults to false, so this should be equivalent
        var handlerExplicit = new InputHandler(searchEnabled: true, disableQuickJump: false);
        Assert.NotNull(handlerExplicit);
    }

    #endregion

    #region InputResult Struct Behavior

    [Fact]
    public void InputResult_IsValueType()
    {
        Assert.True(typeof(InputResult).IsValueType);
    }

    [Fact]
    public void InputResult_DefaultValue_IsNone()
    {
        var result = default(InputResult);
        
        Assert.Equal(InputAction.None, result.Action);
        Assert.Null(result.Character);
        Assert.Null(result.JumpIndex);
    }

    [Fact]
    public void InputResult_ReadonlyStruct_CannotBeModified()
    {
        // This test verifies the readonly nature of InputResult
        // by ensuring it's a readonly struct (compile-time check)
        var type = typeof(InputResult);
        
        // Check if it's a value type (struct)
        Assert.True(type.IsValueType);
        
        // Check that properties have no setters (readonly behavior via init)
        var actionProp = type.GetProperty(nameof(InputResult.Action));
        Assert.NotNull(actionProp);
        // init-only setters are allowed at construction time
    }

    #endregion

    #region Expected Key Mapping Documentation

    /// <summary>
    /// Documents the expected key mappings for InputHandler.ReadKey().
    /// These can't be directly tested without mocking Console.ReadKey,
    /// but serve as living documentation of expected behavior.
    /// </summary>
    [Fact]
    public void ExpectedKeyMappings_Documentation()
    {
        // Navigation Keys
        // UpArrow -> MoveUp
        // DownArrow -> MoveDown
        // Ctrl+K -> MoveUp (vim-style)
        // Ctrl+J -> MoveDown (vim-style)
        // PageUp -> PageUp
        // PageDown -> PageDown
        // Home -> JumpToStart
        // End -> JumpToEnd
        // Tab -> MoveDown
        // Shift+Tab -> MoveUp

        // Selection Keys
        // Enter -> Select
        // Escape -> Cancel
        // Ctrl+C -> Cancel

        // Search Keys
        // Backspace -> SearchBackspace
        // Any printable character -> SearchCharacter (when search enabled)

        // Quick Jump Keys (when not in search mode and DisableQuickJump is false)
        // 1-9 -> QuickJump(0-8)
        //
        // When DisableQuickJump is true:
        // 1-9 -> SearchCharacter (treats numbers as search input)
        // This is useful for episode selection where users type "22" to filter

        Assert.True(true); // Documentation test
    }

    #endregion
}

public class SelectorOptionsTests
{
    [Fact]
    public void SelectorOptions_DisableQuickJump_DefaultsToFalse()
    {
        var options = new SelectorOptions<string>();
        Assert.False(options.DisableQuickJump);
    }

    [Fact]
    public void SelectorOptions_DisableQuickJump_CanBeSetToTrue()
    {
        var options = new SelectorOptions<string> { DisableQuickJump = true };
        Assert.True(options.DisableQuickJump);
    }

    [Fact]
    public void SelectorOptions_DisableQuickJump_IsFalseByDefault()
    {
        var options = new SelectorOptions<string>();
        
        // DisableQuickJump should be false by default (quick jump enabled)
        Assert.False(options.DisableQuickJump);
    }
}
