<!-- SPECIFICATION GOVERNANCE: Governed by Percolator.Desktop/spec/spec.spec.md -->

# Avalonia View Specification (`View.spec.md`)

This specification governs all Avalonia declarative XAML view files (`*.axaml`)—including `Window`, `UserControl`, and `DataTemplate` across `Percolator.Desktop`.

Any file governed by this specification MUST include this header comment on line 1:
```xml
<!-- SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/View.spec.md before modifying this file. -->
```

---

## 1. Quick Rules & Invariants

| ID | Category | MUST DO (Mandatory) | MUST NOT DO (Strictly Prohibited) |
| :-: | :--- | :--- | :--- |
| **[VIEW-001]** | **Compiled Bindings** | Declare `x:DataType="vm:TargetViewModel"` on the root tag **and** on every `<DataTemplate>`. | MUST NOT use untyped reflection bindings (`{Binding ...}` without `x:DataType`). |
| **[VIEW-002]** | **Ancestor Binding Casts** | In compiled bindings targeting an ancestor ViewModel from inside a `<DataTemplate>`, cast `DataContext` explicitly using double parentheses: `{Binding $parent[UserControl].((vm:ParentViewModel)DataContext).Command}`. | MUST NOT omit the typecast (`$parent[UserControl].DataContext.Command`), which causes compiler error `AVLN:0004: Unable to resolve property on 'System.Object'`. |
| **[VIEW-003]** | **Responsive Drawers** | Use native `<SplitView>` with `PanePlacement="Right"` (or `"Left"`), `DisplayMode="Overlay"` (or `"Inline"`), and bind `IsPaneOpen="{Binding IsOpen, Mode=TwoWay}"`. | MUST NOT hand-roll custom slide drawers using raw `Grid`, canvas z-indices, margin animation hacks, or absolute layout. |
| **[VIEW-004]** | **Semantic Dynamic Brushes** | Bind colors and background fills exclusively to semantic Fluent v2 tokens defined in [`Style.spec.md`](./Style.spec.md) using `{DynamicResource ...}` to support runtime theme switching. | MUST NOT hardcode hexadecimal color codes (e.g. `#1E1E1E`, `#FFFFFF`). MUST NOT use `{StaticResource ...}` for theme-dependent brushes. |
| **[VIEW-005]** | **UI Virtualization Integrity** | Place `ListBox` in constrained layouts (e.g. `Grid` row `*` or `DockPanel`) so it manages its own internal `ScrollViewer`. If a list must share a unified scroll area with other elements, use `<ItemsRepeater>` instead. | MUST NOT nest `ListBox` inside an outer unconstrained `ScrollViewer` or vertical `StackPanel` without an explicit `Height` or `MaxHeight` (provides infinite height, destroying virtualization). |
| **[VIEW-006]** | **Corner Radii & Geometry** | Bind all corner radii and control geometry to the semantic tokens defined in [`Style.spec.md`](./Style.spec.md) (`OverlayCornerRadius`, `CardCornerRadius`, `ControlCornerRadius`). | MUST NOT hardcode arbitrary raw integer corner radii (e.g. `CornerRadius="5"`). |
| **[VIEW-007]** | **Grid Text Constraints** | Specify `TextTrimming="CharacterEllipsis"` and/or `TextWrapping="Wrap"` on all `TextBlock` controls hosted in flexible (`*` or `Auto`) grid cells. | MUST NOT allow unbound dynamic text to blow out column widths past the viewport. |
| **[VIEW-008]** | **Declarative Purity** | Declare pure visual presentation and compiled bindings. Delegate user gestures to ViewModel commands (`Command="{Binding RunCommand}"`). | MUST NOT execute business logic, mutate application state, or bind raw pixel dimensions to ViewModels. |
| **[VIEW-009]** | **Declarative Keyboard Accelerators** | Declare global hotkeys and view shortcuts via `<Window.KeyBindings>` or `<UserControl.KeyBindings>` using `<KeyBinding Gesture="Ctrl+K" Command="{Binding ...}" />`. | MUST NOT attach imperative `KeyDown` or `KeyUp` listeners in code-behind to trigger application actions. |

---

## 2. Common AI Traps to Avoid

1. **The Missing `DataTemplate` Scope Trap ([VIEW-001])**:
   * *Trap*: Adding `x:DataType="vm:MainViewModel"` on the root `UserControl` and assuming it cascades into `<ItemsControl.ItemTemplate>`. The items control changes the DataContext of each item, rendering child bindings invalid or untyped.
   * *Fix*: Always add `x:DataType="models:ItemModel"` directly to the child `<DataTemplate>`.
2. **The Ancestor DataContext Reflection Trap ([VIEW-002])**:
   * *Trap*: Writing `{Binding $parent[UserControl].DataContext.RemoveItemCommand}` inside a `DataTemplate`. In compiled bindings, `DataContext` is typed as `System.Object`, causing compilation error `AVLN:0004`.
   * *Fix*: Explicitly cast the ancestor DataContext: `{Binding $parent[UserControl].((vm:MainViewModel)DataContext).RemoveItemCommand}`.
3. **The StaticResource Theme Freeze Trap ([VIEW-004])**:
   * *Trap*: Writing `Background="{StaticResource CardBackgroundFillColorDefaultBrush}"`. When the user toggles between Dark and Light mode at runtime, the control remains frozen in its initial theme brush.
   * *Fix*: Always use `{DynamicResource ...}` for brush and color tokens.
4. **The Infinite Measure Virtualization Destruction Trap ([VIEW-005])**:
   * *Trap*: Wrapping a `ListBox` inside an outer `<ScrollViewer>` or vertical `<StackPanel>`. The outer container gives the `ListBox` infinite vertical space (`double.PositiveInfinity`), causing `VirtualizingStackPanel` to instantiate hundreds or thousands of container elements at once, crashing performance.
   * *Fix*: Let `ListBox` handle its own scrolling in a `Grid` row with `Height="*"`, or use `<ItemsRepeater>` if unified scrolling with outer content is required.
5. **The Custom Drawer Reinvention Trap ([VIEW-003])**:
   * *Trap*: Building an overlay sidebar using custom column animations, negative margins, or absolute Canvas positioning.
   * *Fix*: Use `<SplitView DisplayMode="Overlay" PanePlacement="Right">` which has built-in smooth slide animations, overlay scrim, and light-dismiss.
6. **The Grid Column Viewport Blowout Trap ([VIEW-007])**:
   * *Trap*: Putting `<TextBlock Text="{Binding LongUserMessage}" />` inside a `Grid` with `ColumnDefinitions="Auto, *"` without `TextWrapping="Wrap"`. A long message without spaces pushes the entire window beyond the screen boundary.
   * *Fix*: Always set `TextWrapping="Wrap"` and `TextTrimming="CharacterEllipsis"` on dynamic text containers.
7. **The Imperative KeyDown Handler Trap ([VIEW-009])**:
   * *Trap*: Wiring `KeyDown += OnWindowKeyDown` in code-behind to check `if (e.Key == Key.Escape) Close();`.
   * *Fix*: Declare `<Window.KeyBindings><KeyBinding Gesture="Escape" Command="{Binding CancelCommand}" /></Window.KeyBindings>`.

---

## 3. Micro-Diff Examples: Bad vs. Good

### 3.1 Compiled Bindings on Root & DataTemplate ([VIEW-001])
```xml
<!-- ❌ BAD: Untyped binding; breaks compile-time safety and Native AOT -->
<UserControl x:Class="Percolator.Desktop.Features.Chat.ChatView">
    <ItemsControl ItemsSource="{Binding Messages}">
        <ItemsControl.ItemTemplate>
            <DataTemplate>
                <TextBlock Text="{Binding Content}" />
            </DataTemplate>
        </ItemsControl.ItemTemplate>
    </ItemsControl>
</UserControl>

<!-- ✅ GOOD: Explicitly typed compiled bindings at root and template levels -->
<UserControl x:Class="Percolator.Desktop.Features.Chat.ChatView"
             xmlns:vm="using:Percolator.Desktop.Features.Chat"
             xmlns:m="using:Percolator.Desktop.Features.Chat.Models"
             x:DataType="vm:ChatViewModel">
    <ItemsControl ItemsSource="{Binding Messages}">
        <ItemsControl.ItemTemplate>
            <DataTemplate x:DataType="m:ChatMessageModel">
                <TextBlock Text="{Binding Content}" />
            </DataTemplate>
        </ItemsControl.ItemTemplate>
    </ItemsControl>
</UserControl>
```

### 3.2 Ancestor DataContext Type-Casting ([VIEW-002])
```xml
<!-- ❌ BAD: Missing explicit cast; compiler fails with AVLN:0004 -->
<DataTemplate x:DataType="m:ConversationModel">
    <Button Content="Leave"
            Command="{Binding $parent[UserControl].DataContext.LeaveConversationCommand}"
            CommandParameter="{Binding}" />
</DataTemplate>

<!-- ✅ GOOD: Double parentheses typecast enables compiled binding to ancestor ViewModel -->
<DataTemplate x:DataType="m:ConversationModel">
    <Button Content="Leave"
            Command="{Binding $parent[UserControl].((vm:ChatViewModel)DataContext).LeaveConversationCommand}"
            CommandParameter="{Binding}" />
</DataTemplate>
```

### 3.3 Semantic Dynamic Theming ([VIEW-004], [VIEW-006])
```xml
<!-- ❌ BAD: Hardcoded dark theme hex colors, static brush resource, and manual corner radius -->
<Border Background="#2F3136" 
        BorderBrush="{StaticResource SystemAccentColor}"
        CornerRadius="4" 
        Padding="12">
    <TextBlock Text="Settings" Foreground="#FFFFFF" />
</Border>

<!-- ✅ GOOD: Dynamic Fluent v2 tokens adapting seamlessly across Light and Dark themes -->
<Border Background="{DynamicResource CardBackgroundFillColorDefaultBrush}"
        BorderBrush="{DynamicResource DividerStrokeColorDefaultBrush}"
        BorderThickness="1"
        CornerRadius="{StaticResource CardCornerRadius}"
        Padding="16,12">
    <TextBlock Text="Settings" 
                Foreground="{DynamicResource TextFillColorPrimaryBrush}"
                Theme="{StaticResource SubtitleTextBlockStyle}" />
</Border>
```

### 3.4 Responsive Sidebars & Drawers (Native `SplitView`) ([VIEW-003])
```xml
<!-- ❌ BAD: Reinventing drawer overlay using canvas, z-index, and margin hacks -->
<Grid>
    <Canvas ZIndex="99" Margin="{Binding DrawerMargin}">
        <Border Width="300" Background="#1E1E1E" />
    </Canvas>
    <Grid><!-- Main Content --></Grid>
</Grid>

<!-- ✅ GOOD: Standard SplitView with hardware-accelerated animations and light dismiss -->
<SplitView x:Name="InspectorSplitView"
           PanePlacement="Right"
           OpenPaneLength="320"
           DisplayMode="Overlay"
           IsPaneOpen="{Binding IsInspectorOpen, Mode=TwoWay}">
    <SplitView.Pane>
        <Border Background="{DynamicResource SolidBackgroundFillColorBaseBrush}"
                BorderBrush="{DynamicResource DividerStrokeColorDefaultBrush}"
                BorderThickness="1,0,0,0">
            <!-- Inspector / Sidebar View -->
        </Border>
    </SplitView.Pane>

    <Grid ColumnDefinitions="72,260,*">
        <!-- Main Shell Body -->
    </Grid>
</SplitView>
```

### 3.5 UI Virtualization Integrity ([VIEW-005])
```xml
<!-- ❌ BAD: ListBox inside outer ScrollViewer destroys VirtualizingStackPanel -->
<ScrollViewer>
    <StackPanel>
        <TextBlock Text="Recent Conversations" />
        <ListBox ItemsSource="{Binding Conversations}" /> <!-- VIOLATION: Infinite height! -->
    </StackPanel>
</ScrollViewer>

<!-- ✅ GOOD: ListBox constrained in a Grid cell handles its own virtualization -->
<Grid RowDefinitions="Auto, *">
    <TextBlock Grid.Row="0" Text="Recent Conversations" Margin="12,8" />
    <ListBox Grid.Row="1" ItemsSource="{Binding Conversations}" />
</Grid>
```

### 3.6 Declarative Keyboard Accelerators ([VIEW-009])
```xml
<!-- ❌ BAD: Handling hotkeys imperatively in code-behind KeyDown events -->
// In code-behind: KeyDown += (_, e) => { if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.K) ... };

<!-- ✅ GOOD: Declarative XAML KeyBindings bound to ViewModel command -->
<UserControl.KeyBindings>
    <KeyBinding Gesture="Ctrl+K" Command="{Binding OpenCommandPaletteCommand}" />
    <KeyBinding Gesture="Ctrl+Enter" Command="{Binding SendMessageCommand}" />
    <KeyBinding Gesture="Escape" Command="{Binding CloseDrawerCommand}" />
</UserControl.KeyBindings>
```

---

## 4. Citations & Verified Sources

* **Avalonia Compiled Bindings**: [Avalonia Documentation – Compiled Bindings](https://docs.avaloniaui.net/docs/data-binding/compiledbindings). Specifies `x:DataType`, compile-time binding validation, and parent DataContext casting syntax `((vm:Type)DataContext)`.
* **Avalonia SplitView Control**: [Avalonia Documentation – SplitView](https://docs.avaloniaui.net/docs/reference/controls/splitview). Documents native overlay/inline drawer navigation, pane placement, and light-dismiss behavior.
* **Avalonia UI Virtualization**: [Avalonia Documentation – Virtualization](https://docs.avaloniaui.net/docs/concepts/ui-virtualization). Explains viewport measurement mechanics, `VirtualizingStackPanel`, and the prohibition against nesting virtualized controls in unconstrained `ScrollViewer` containers.
* **Avalonia ItemsRepeater Control**: [Avalonia Documentation – ItemsRepeater](https://docs.avaloniaui.net/docs/reference/controls/itemsrepeater). Specifies coordinated virtualization with an external scroll container.
* **Avalonia Resource Resolution**: [Avalonia Documentation – Resources](https://docs.avaloniaui.net/docs/styling/resources). Establishes `{DynamicResource}` requirement for dynamic theme tokens vs `{StaticResource}` for immutable metrics.
* **Avalonia Input & KeyBindings**: [Avalonia Documentation – KeyBindings](https://docs.avaloniaui.net/docs/concepts/input/hotkeys). Documents declarative keyboard gestures and command invocation.
* **FluentAvalonia Theming & Design Tokens**: [FluentAvalonia UI Guidelines](https://github.com/amwx/FluentAvalonia). Canonical reference for WinUI/Fluent v2 semantic brush and corner radius tokens.
