<!-- SPECIFICATION GOVERNANCE: Governed by Percolator.Desktop/spec/spec.spec.md -->

# Avalonia Style Specification (`Style.spec.md`)

This specification governs all styling, control themes, resource dictionaries, and geometry tokens across `Percolator.Desktop`—including `App.axaml`, `Spacing.axaml`, and custom control themes.

Any file governed by this specification MUST include this header comment on line 1:
```xml
<!-- SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/Style.spec.md before modifying this file. -->
```

---

## 1. Quick Rules & Invariants

| ID | Category | MUST DO (Mandatory) | MUST NOT DO (Strictly Prohibited) |
| :-: | :--- | :--- | :--- |
| **[STYLE-001]** | **`ControlTheme` vs `Style`** | Declare baseline visual templates inside `<ControlTheme TargetType="...">` within a `<ResourceDictionary>`. Use `<Style>` exclusively for additive, cascading utility classes. | MUST NOT put a `ControlTemplate` inside a global `<Style Selector="Button">` (breaks theme replacement and cascades uncontrollably). |
| **[STYLE-002]** | **Local Value Priority** | Apply visual brushes and colors via `ControlTheme` or `Style` classes. Reserve inline XAML attributes on control tags strictly for structural layout (`Grid.Row`, `Width`, `Orientation`). | MUST NOT set inline visual properties (e.g. `<Button Background="Red" />`) on stateful controls. Direct tag assignments evaluate at `LocalValue` priority (Priority 2), permanently disabling `:pointerover` and `:pressed` triggers (Priority 3). |
| **[STYLE-003]** | **Sole Theme Engine** | Configure `<sty:FluentAvaloniaTheme PreferUserAccentColor="True" PreferSystemTheme="True" />` in `App.axaml` as the application's sole theme provider. | MUST NOT import standard Avalonia `<FluentTheme />` or `<SimpleTheme />`. Mixing theme engines causes dual resource trees, broken acrylics, and dark mode failure. |
| **[STYLE-004]** | **Caret Nesting & Part Targets** | Nest interactive state selectors using caret syntax (`^:pointerover`, `^:pressed`, `^:disabled`). Target internal template presenters with `/template/` when styling controls with complex visual parts (e.g. `^:pointerover /template/ ContentPresenter`). | MUST NOT declare flat, repetitive selectors like `Button.my-button:pointerover`. MUST NOT omit `/template/` when overriding template part backgrounds. |
| **[STYLE-005]** | **Theme Dictionaries** | Declare custom palette brushes within `<ResourceDictionary.ThemeDictionaries>` under `Default` (Dark) and `Light` keys. Reference dynamic brushes using `{DynamicResource ...}`. | MUST NOT declare flat static brushes in the root dictionary for theme-variant colors. MUST NOT use `{StaticResource ...}` on brushes that adapt between Light and Dark mode. |
| **[STYLE-006]** | **Typography Hierarchy** | Apply text typography using standard WinUI text styles (`Theme="{StaticResource SubtitleTextBlockStyle}"`, `BodyStrongTextBlockStyle`, `CaptionTextBlockStyle`). | MUST NOT set arbitrary ad-hoc combinations of `FontSize`, `FontWeight`, and `LineHeight` directly on individual `TextBlock` instances. |
| **[STYLE-007]** | **Centralized Geometry Tokens** | Centralize all corner radii in `Common/Styles/Spacing.axaml` (`OverlayCornerRadius`, `CardCornerRadius`, `ControlCornerRadius`) and reference them via `{StaticResource ...}`. | MUST NOT hardcode raw integer corner radii (e.g. `CornerRadius="6"`) in view templates. |
| **[STYLE-008]** | **Style Class Scoping** | Target styles using scoped class selectors (e.g. `Button.danger`, `TextBox.search`). | MUST NOT write global unclassed element selectors (e.g. `<Style Selector="Button">`) that set broad visual properties like `Padding` or `Margin`, which bleeds into dialogs and chrome. |

---

## 2. Common AI Traps to Avoid

1. **The Inline Tag Attribute Trigger Suppression Trap ([STYLE-002])**:
   * *Trap*: Writing `<Button Background="DarkRed" Content="Delete" />` and wondering why the button doesn't respond visually when hovered or clicked.
   * *Why*: Avalonia evaluates property values via `BindingPriority`. Direct tag attributes are assigned `LocalValue` priority (Priority 2). Pseudo-classes like `:pointerover` operate at `StyleTrigger` priority (Priority 3). The local value permanently masks the hover trigger!
   * *Fix*: Define a style class (e.g. `Button.danger`) where the baseline background is set by a style setter (Priority 5), allowing `:pointerover` (Priority 3) to smoothly override it.
2. **The Theme Engine Collision Trap ([STYLE-003])**:
   * *Trap*: Adding `<FluentTheme />` to `App.axaml` after reading standard Avalonia tutorials while `FluentAvaloniaTheme` is already present.
   * *Fix*: Keep exclusively `<sty:FluentAvaloniaTheme />` in `App.axaml`.
3. **The Silent Template Target Miss Trap ([STYLE-004])**:
   * *Trap*: Writing `<Style Selector="Button.accent:pointerover"><Setter Property="Background" Value="Blue" /></Style>`.
   * *Why*: In Fluent controls, the button background is rendered by an inner `ContentPresenter#PART_ContentPresenter`. Built-in theme triggers color the inner presenter directly, completely ignoring background setters on the outer `Button`.
   * *Fix*: Target the inner template presenter: `<Style Selector="Button.accent:pointerover /template/ ContentPresenter">`.
4. **The Flat Root Resource Theme Lock Trap ([STYLE-005])**:
   * *Trap*: Defining `<SolidColorBrush x:Key="AppHeaderBrush" Color="#1E1E1E" />` in the root `<Application.Resources>` dictionary.
   * *Fix*: Put variant-aware colors into `<ResourceDictionary.ThemeDictionaries>` with `<ResourceDictionary x:Key="Default">` (Dark) and `<ResourceDictionary x:Key="Light">` sub-dictionaries.
5. **The Global Element Selector Bleed Trap ([STYLE-008])**:
   * *Trap*: Writing `<Style Selector="Button"><Setter Property="Padding" Value="16,8" /></Style>` to adjust spacing in a feature view.
   * *Consequence*: This pollutes every button across the entire process, including window title bar buttons, date picker arrows, and flyout dismiss buttons.
   * *Fix*: Always scope styles to a class: `<Style Selector="Button.padded">`.

---

## 3. Micro-Diff Examples: Bad vs. Good

### 3.1 ControlTheme for Templates vs. Style ([STYLE-001])
```xml
<!-- ❌ BAD: Putting ControlTemplate in a Style breaks theme replacement and leaks globally -->
<Style Selector="Button">
    <Setter Property="Template">
        <ControlTemplate>
            <Border Background="Blue" />
        </ControlTemplate>
    </Setter>
</Style>

<!-- ✅ GOOD: Baseline ControlTemplates belong in ControlTheme in ResourceDictionary -->
<ResourceDictionary>
    <ControlTheme x:Key="ActionButtonTheme" TargetType="Button">
        <Setter Property="Background" Value="{DynamicResource AccentFillColorDefaultBrush}" />
        <Setter Property="Foreground" Value="{DynamicResource TextOnAccentFillColorPrimaryBrush}" />
        <Setter Property="CornerRadius" Value="{StaticResource ControlCornerRadius}" />
        <Setter Property="Template">
            <ControlTemplate>
                <ContentPresenter Name="PART_ContentPresenter"
                                  Background="{TemplateBinding Background}"
                                  Foreground="{TemplateBinding Foreground}"
                                  CornerRadius="{TemplateBinding CornerRadius}"
                                  Content="{TemplateBinding Content}"
                                  Padding="{TemplateBinding Padding}" />
            </ControlTemplate>
        </Setter>
    </ControlTheme>
</ResourceDictionary>
```

### 3.2 Preserving Interactive Triggers vs. Inline Tag Values ([STYLE-002])
```xml
<!-- ❌ BAD: Inline Background has LocalValue priority (Priority 2); breaks hover/press triggers -->
<Button Background="{DynamicResource SystemFillColorCriticalBrush}"
        Content="Delete Persona" />

<!-- ✅ GOOD: Scoped style class allows :pointerover triggers (Priority 3) to function -->
<!-- In Styles: -->
<Style Selector="Button.danger">
    <Setter Property="Background" Value="{DynamicResource SystemFillColorCriticalBrush}" />
    <Setter Property="Foreground" Value="White" />
    <Style Selector="^:pointerover /template/ ContentPresenter">
        <Setter Property="Background" Value="{DynamicResource SystemFillColorCriticalBackgroundBrush}" />
    </Style>
</Style>

<!-- In View: -->
<Button Classes="danger" Content="Delete Persona" />
```

### 3.3 Pseudo-Class Caret Nesting & Template Part Targeting ([STYLE-004])
```xml
<!-- ❌ BAD: Flat, non-nested selector declaration omitting template target -->
<Style Selector="Button.accent:pointerover">
    <Setter Property="Background" Value="DarkBlue" /> <!-- Silently ignored by built-in template! -->
</Style>

<!-- ✅ GOOD: Caret (^) nesting targeting inner template ContentPresenter -->
<Style Selector="Button.accent">
    <Setter Property="Background" Value="{DynamicResource AccentFillColorDefaultBrush}" />
    <Setter Property="Foreground" Value="{DynamicResource TextOnAccentFillColorPrimaryBrush}" />
    <Style Selector="^:pointerover /template/ ContentPresenter">
        <Setter Property="Background" Value="{DynamicResource AccentFillColorSecondaryBrush}" />
    </Style>
    <Style Selector="^:pressed /template/ ContentPresenter">
        <Setter Property="Background" Value="{DynamicResource AccentFillColorTertiaryBrush}" />
    </Style>
</Style>
```

### 3.4 Theme Dictionaries for Adaptive Brushes ([STYLE-005])
```xml
<!-- ❌ BAD: Flat brush in root dictionary cannot adapt to Light/Dark mode changes -->
<ResourceDictionary>
    <SolidColorBrush x:Key="SidebarBackgroundBrush" Color="#202225" />
</ResourceDictionary>

<!-- ✅ GOOD: ThemeDictionaries provide native light/dark variant adaptation -->
<ResourceDictionary>
    <ResourceDictionary.ThemeDictionaries>
        <ResourceDictionary x:Key="Default"> <!-- Dark Variant -->
            <SolidColorBrush x:Key="SidebarBackgroundBrush" Color="#1E1E1E" />
        </ResourceDictionary>
        <ResourceDictionary x:Key="Light"> <!-- Light Variant -->
            <SolidColorBrush x:Key="SidebarBackgroundBrush" Color="#F3F3F3" />
        </ResourceDictionary>
    </ResourceDictionary.ThemeDictionaries>
</ResourceDictionary>
```

### 3.5 Centralized Geometry Tokens (`Spacing.axaml`) ([STYLE-007])
```xml
<!-- ✅ GOOD: Centralized immutable geometry tokens -->
<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <CornerRadius x:Key="OverlayCornerRadius">8</CornerRadius>
    <CornerRadius x:Key="CardCornerRadius">6</CornerRadius>
    <CornerRadius x:Key="ControlCornerRadius">4</CornerRadius>
    
    <Thickness x:Key="CardPadding">16,12</Thickness>
    <Thickness x:Key="OverlayPadding">20</Thickness>
</ResourceDictionary>
```

---

## 4. Citations & Verified Sources

* **Avalonia Control Themes**: [Avalonia Documentation – Control Themes](https://docs.avaloniaui.net/docs/styling/control-themes). Specifies `ControlTheme`, `TargetType`, template replacement, and `BasedOn` theme inheritance.
* **Avalonia Binding Priority & Precedence**: [Avalonia Documentation – Binding Priority](https://docs.avaloniaui.net/docs/concepts/binding-priority). Details the priority scale (`Animation` > `LocalValue` > `StyleTrigger` > `Template` > `Style`) and explains why inline attributes suppress style triggers.
* **Avalonia Style Selectors & Caret Syntax**: [Avalonia Documentation – Style Selectors](https://docs.avaloniaui.net/docs/styling/styles). Covers selector syntax, the caret (`^`) nesting operator, and template traversal with `/template/`.
* **Avalonia Theme Variants**: [Avalonia Documentation – Theme Variants](https://docs.avaloniaui.net/docs/styling/theme-variants). Documents `<ResourceDictionary.ThemeDictionaries>` with `Default`, `Light`, and `Dark` keys.
* **FluentAvalonia Styling Architecture**: [FluentAvalonia UI Guidelines](https://github.com/amwx/FluentAvalonia). Canonical documentation for `FluentAvaloniaTheme`, WinUI / Fluent v2 brush hierarchy, and typography styles.
