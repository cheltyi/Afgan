using System.Linq;
using System.Text;
using Content.Client.Materials;
using Content.Shared.Lathe;
using Content.Shared.Lathe.Prototypes;
using Content.Shared.Materials;
using Content.Shared.Research.Prototypes;
using Robust.Client.AutoGenerated;
using Robust.Client.GameObjects;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Client.UserInterface.XAML;
using Robust.Client.ResourceManagement;
using Robust.Client.Graphics;
using Robust.Shared.Prototypes;

namespace Content.Client.Lathe.UI;

[GenerateTypedNameReferences]
public sealed partial class LatheMenu : DefaultWindow
{
    [Dependency] private readonly IEntityManager _entityManager = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly IResourceCache _resources = default!;

    private EntityUid _owner;
    private readonly SpriteSystem _spriteSystem;
    private readonly LatheSystem _lathe;
    private readonly MaterialStorageSystem _materialStorage;

    public event Action<BaseButton.ButtonEventArgs>? OnServerListButtonPressed;
    public event Action<string, int>? RecipeQueueAction;

    public List<ProtoId<LatheRecipePrototype>> Recipes = new();

    public List<ProtoId<LatheCategoryPrototype>>? Categories;

    public ProtoId<LatheCategoryPrototype>? CurrentCategory;

    public LatheMenu(LatheBoundUserInterface owner)
    {
        _owner = owner.Owner;
        RobustXamlLoader.Load(this);
        IoCManager.InjectDependencies(this);

        _spriteSystem = _entityManager.System<SpriteSystem>();
        _lathe = _entityManager.System<LatheSystem>();
        _materialStorage = _entityManager.System<MaterialStorageSystem>();

        Title = _entityManager.GetComponent<MetaDataComponent>(owner.Owner).EntityName;

        SearchBar.OnTextChanged += _ =>
        {
            PopulateRecipes();
        };
        AmountLineEdit.OnTextChanged += _ =>
        {
            PopulateRecipes();
        };

        FilterOption.OnItemSelected += OnItemSelected;

        ServerListButton.OnPressed += a => OnServerListButtonPressed?.Invoke(a);

        if (_entityManager.TryGetComponent<LatheComponent>(owner.Owner, out var latheComponent))
        {
            if (!latheComponent.DynamicRecipes.Any())
            {
                ServerListButton.Visible = false;
            }
        }

        MaterialsList.SetOwner(owner.Owner);
    }

    /// <summary>
    /// Populates the list of all the recipes
    /// </summary>
    public void PopulateRecipes()
    {
        var recipesToShow = new List<LatheRecipePrototype>();
        foreach (var recipe in Recipes)
        {
            if (!_prototypeManager.TryIndex(recipe, out var proto))
                continue;

            if (CurrentCategory != null && proto.Category != CurrentCategory)
                continue;

            if (SearchBar.Text.Trim().Length != 0)
            {
                if (proto.Name.ToLowerInvariant().Contains(SearchBar.Text.Trim().ToLowerInvariant()))
                    recipesToShow.Add(proto);
            }
            else
            {
                recipesToShow.Add(proto);
            }
        }

        if (!int.TryParse(AmountLineEdit.Text, out var quantity) || quantity <= 0)
            if (quantity >= 1)
                quantity = 1;

        var sortedRecipesToShow = recipesToShow.OrderBy(p => p.Name);
        RecipeList.Children.Clear();
        foreach (var prototype in sortedRecipesToShow)
        {
            EntityPrototype? recipeProto = null;
            if (_prototypeManager.TryIndex(prototype.Result, out EntityPrototype? entityProto) && entityProto != null)
                recipeProto = entityProto;

            var canProduce = _lathe.CanProduce(_owner, prototype, quantity);

            var control = new RecipeControl(prototype, () => GenerateTooltipText(prototype), canProduce, recipeProto);
            control.OnButtonPressed += s =>
            {
                if (!int.TryParse(AmountLineEdit.Text, out var amount) || amount <= 0)
                    amount = 1;
                RecipeQueueAction?.Invoke(s, amount);
            };
            RecipeList.AddChild(control);
        }
    }

    private string GenerateTooltipText(LatheRecipePrototype prototype)
    {
        StringBuilder sb = new();

        foreach (var (id, amount) in prototype.RequiredMaterials)
        {
            if (!_prototypeManager.TryIndex<MaterialPrototype>(id, out var proto))
                continue;

            var adjustedAmount = SharedLatheSystem.AdjustMaterial(amount, prototype.ApplyMaterialDiscount, _entityManager.GetComponent<LatheComponent>(_owner).MaterialUseMultiplier);
            var sheetVolume = _materialStorage.GetSheetVolume(proto);

            var unit = Loc.GetString(proto.Unit);
            var sheets = adjustedAmount / (float) sheetVolume;

            var availableAmount = _materialStorage.GetMaterialAmount(_owner, id);
            var missingAmount = Math.Max(0, adjustedAmount - availableAmount);
            var missingSheets = missingAmount / (float) sheetVolume;

            var name = Loc.GetString(proto.Name);

            string tooltipText;
            if (missingSheets > 0)
            {
                tooltipText = Loc.GetString("lathe-menu-material-amount-missing", ("amount", sheets), ("missingAmount", missingSheets), ("unit", unit), ("material", name));
            }
            else
            {
                var amountText = Loc.GetString("lathe-menu-material-amount", ("amount", sheets), ("unit", unit));
                tooltipText = Loc.GetString("lathe-menu-tooltip-display", ("material", name), ("amount", amountText));
            }

            sb.AppendLine(tooltipText);
        }

        if (!string.IsNullOrWhiteSpace(prototype.Description))
            sb.AppendLine(Loc.GetString("lathe-menu-description-display", ("description", prototype.Description)));

        // Remove last newline
        if (sb.Length > 0)
            sb.Remove(sb.Length - 1, 1);

        return sb.ToString();
    }

    public void UpdateCategories()
    {
        var currentCategories = new List<ProtoId<LatheCategoryPrototype>>();
        foreach (var recipeId in Recipes)
        {
            var recipe = _prototypeManager.Index(recipeId);

            if (recipe.Category == null)
                continue;

            if (currentCategories.Contains(recipe.Category.Value))
                continue;

            currentCategories.Add(recipe.Category.Value);
        }

        if (Categories != null && (Categories.Count == currentCategories.Count || !Categories.All(currentCategories.Contains)))
            return;

        Categories = currentCategories;
        var sortedCategories = currentCategories
            .Select(p => _prototypeManager.Index(p))
            .OrderBy(p => Loc.GetString(p.Name))
            .ToList();

        FilterOption.Clear();
        FilterOption.AddItem(Loc.GetString("lathe-menu-category-all"), -1);
        foreach (var category in sortedCategories)
        {
            FilterOption.AddItem(Loc.GetString(category.Name), Categories.IndexOf(category.ID));
        }

        FilterOption.SelectId(-1);
    }

    /// <summary>
    /// Populates the build queue list with all queued items
    /// </summary>
    /// <param name="queue"></param>
    public void PopulateQueueList(List<LatheRecipePrototype> queue)
    {
        QueueList.DisposeAllChildren();

        var idx = 1;
        foreach (var recipe in queue)
        {
            var queuedRecipeBox = new BoxContainer();
            queuedRecipeBox.Orientation = BoxContainer.LayoutOrientation.Horizontal;

            var queuedRecipeProto = new EntityPrototypeView();
            queuedRecipeBox.AddChild(queuedRecipeProto);
            if (_prototypeManager.TryIndex(recipe.Result, out EntityPrototype? entityProto) && entityProto != null)
                queuedRecipeProto.SetPrototype(entityProto);

            var queuedRecipeLabel = new Label();
            queuedRecipeLabel.Text = $"{idx}. {recipe.Name}";
            queuedRecipeBox.AddChild(queuedRecipeLabel);
            QueueList.AddChild(queuedRecipeBox);
            idx++;
        }
    }

    public void SetQueueInfo(LatheRecipePrototype? recipe)
    {
        FabricatingContainer.Visible = recipe != null;
        if (recipe == null)
            return;

        if (_prototypeManager.TryIndex(recipe.Result, out EntityPrototype? entityProto) && entityProto != null)
            FabricatingEntityProto.SetPrototype(entityProto);

        NameLabel.Text = $"{recipe.Name}";
    }

    private void OnItemSelected(OptionButton.ItemSelectedEventArgs obj)
    {
        FilterOption.SelectId(obj.Id);
        if (obj.Id == -1)
        {
            CurrentCategory = null;
        }
        else
        {
            CurrentCategory = Categories?[obj.Id];
        }
        PopulateRecipes();
    }
}