using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ComboBox = System.Windows.Controls.ComboBox;
using Grid = System.Windows.Controls.Grid;
using Point = System.Windows.Point;

namespace ViewScheduleSheetSpreader
{
    public partial class ViewScheduleSheetSpreaderWPF : Window
    {
        private readonly Document _doc;
        private Point _dragStartPoint;
        /// <summary>Все спецификации в проекте.</summary>
        private readonly List<ViewSchedule> _allViewSchedules;

        /// <summary>Коллекция для верхнего списка (с учётом поиска и выбранных).</summary>
        private ObservableCollection<ViewSchedule> ViewScheduleInProjectCollection;

        /// <summary>Текущая строка поиска по спецификациям.</summary>
        private string _searchText = string.Empty;

        private readonly AlphanumComparatorFastString _nameComparer = new AlphanumComparatorFastString();

        public ObservableCollection<ViewSchedule> SelectedViewScheduleCollection;

        ObservableCollection<Family> TitleBlocksForFirstSheetCollection;
        ObservableCollection<Family> TitleBlocksForFollowingSheetsCollection;
        ObservableCollection<FamilySymbol> TitleBlocksForFirstSheetTypeCollection;
        ObservableCollection<FamilySymbol> TitleBlocksForFollowingSheetsTypeCollection;
        ObservableCollection<Parameter> FamilyInstanceParametersCollection;

        public FamilySymbol FirstSheetType;
        public FamilySymbol FollowingSheetType;
        public Parameter SheetFormatParameter;
        public Definition GroupingParameterDefinition;
        public string SheetSizeVariantName;
        public double XOffset;   // оставляем для совместимости с командой
        public double YOffset;   // оставляем для совместимости с командой
        public int FirstSheetNumber;
        public string HeaderInSpecificationHeaderVariantName;
        public double SpecificationHeaderHeight;
        public string SheetNumberSuffix;

        ViewScheduleSheetSpreaderSettings ViewScheduleSheetSpreaderSettingsItem;
        List<string> SelectedViewScheduleList;

        public ViewScheduleSheetSpreaderWPF(Document doc, List<ViewSchedule> viewScheduleList, List<Family> titleBlockFamilysList)
        {
            _doc = doc;

            _allViewSchedules = viewScheduleList ?? new List<ViewSchedule>();
            ViewScheduleInProjectCollection = new ObservableCollection<ViewSchedule>();
            SelectedViewScheduleCollection = new ObservableCollection<ViewSchedule>();

            TitleBlocksForFirstSheetCollection = new ObservableCollection<Family>(titleBlockFamilysList ?? new List<Family>());
            TitleBlocksForFollowingSheetsCollection = new ObservableCollection<Family>(titleBlockFamilysList ?? new List<Family>());

            ViewScheduleSheetSpreaderSettingsItem = new ViewScheduleSheetSpreaderSettings().GetSettings();
            SelectedViewScheduleList = new ViewScheduleListToXML().GetSettings();

            InitializeComponent();

            // Верхний список (спецификации в проекте)
            listBox_ViewScheduleInProjectCollection.ItemsSource = ViewScheduleInProjectCollection;
            listBox_ViewScheduleInProjectCollection.DisplayMemberPath = "Name";

            // Нижний список (спецификации для размещения)
            listBox_SelectedViewScheduleCollection.ItemsSource = SelectedViewScheduleCollection;
            listBox_SelectedViewScheduleCollection.DisplayMemberPath = "Name";

            // Семейства рамок
            comboBox_FirstSheetFamily.ItemsSource = TitleBlocksForFirstSheetCollection;
            comboBox_FirstSheetFamily.DisplayMemberPath = "Name";

            comboBox_FollowingSheetsFamily.ItemsSource = TitleBlocksForFollowingSheetsCollection;
            comboBox_FollowingSheetsFamily.DisplayMemberPath = "Name";

            // восстановление настроек
            SetSavedSettingsValueToForm();

            // первичная сборка верхнего списка
            RebuildAvailableViewSchedules();
        }

        #region Добавить / Убрать спецификации

        private void btn_Add_Click(object sender, RoutedEventArgs e)
        {
            var selected = listBox_ViewScheduleInProjectCollection
                .SelectedItems
                .Cast<ViewSchedule>()
                .ToList();

            foreach (ViewSchedule vs in selected)
            {
                if (!SelectedViewScheduleCollection.Contains(vs))
                {
                    SelectedViewScheduleCollection.Add(vs);
                }
            }

            RebuildAvailableViewSchedules();
        }

        private void btn_Exclude_Click(object sender, RoutedEventArgs e)
        {
            var selected = listBox_SelectedViewScheduleCollection
                .SelectedItems
                .Cast<ViewSchedule>()
                .ToList();

            foreach (ViewSchedule vs in selected)
            {
                SelectedViewScheduleCollection.Remove(vs);
            }

            RebuildAvailableViewSchedules();
        }

        #endregion

        #region Комбобоксы рамок

        private void comboBox_FirstSheetFamily_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var family = (sender as ComboBox)?.SelectedItem as Family;
            if (family == null)
                return;

            List<ElementId> familySymbolsIdList = family.GetFamilySymbolIds().ToList();
            TitleBlocksForFirstSheetTypeCollection = new ObservableCollection<FamilySymbol>();

            if (familySymbolsIdList.Count != 0)
            {
                foreach (ElementId symbolId in familySymbolsIdList)
                {
                    TitleBlocksForFirstSheetTypeCollection.Add(_doc.GetElement(symbolId) as FamilySymbol);
                }
            }

            TitleBlocksForFirstSheetTypeCollection =
                new ObservableCollection<FamilySymbol>(TitleBlocksForFirstSheetTypeCollection.OrderBy(fs => fs.Name).ToList());

            comboBox_FirstSheetType.ItemsSource = TitleBlocksForFirstSheetTypeCollection;
            comboBox_FirstSheetType.DisplayMemberPath = "Name";

            if (comboBox_FirstSheetType.Items.Count > 0)
                comboBox_FirstSheetType.SelectedItem = comboBox_FirstSheetType.Items.GetItemAt(0);
        }

        private void comboBox_FollowingSheetsFamily_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var family = (sender as ComboBox)?.SelectedItem as Family;
            if (family == null)
                return;

            List<ElementId> familySymbolsIdList = family.GetFamilySymbolIds().ToList();
            TitleBlocksForFollowingSheetsTypeCollection = new ObservableCollection<FamilySymbol>();

            if (familySymbolsIdList.Count != 0)
            {
                foreach (ElementId symbolId in familySymbolsIdList)
                {
                    TitleBlocksForFollowingSheetsTypeCollection.Add(_doc.GetElement(symbolId) as FamilySymbol);
                }
            }

            TitleBlocksForFollowingSheetsTypeCollection =
                new ObservableCollection<FamilySymbol>(TitleBlocksForFollowingSheetsTypeCollection.OrderBy(fs => fs.Name).ToList());

            comboBox_FollowingSheetsType.ItemsSource = TitleBlocksForFollowingSheetsTypeCollection;
            comboBox_FollowingSheetsType.DisplayMemberPath = "Name";

            if (comboBox_FollowingSheetsType.Items.Count > 0)
                comboBox_FollowingSheetsType.SelectedItem = comboBox_FollowingSheetsType.Items.GetItemAt(0);
        }

        private void comboBox_FirstSheetType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            FamilySymbol selectedFamilySymbol = (sender as ComboBox)?.SelectedItem as FamilySymbol;
            if (selectedFamilySymbol == null)
                return;

            ParameterSet parameterSet = null;

            ElementClassFilter filter = new ElementClassFilter(typeof(FamilyInstance));
            IList<ElementId> selectedFamilyInstanceList = selectedFamilySymbol.GetDependentElements(filter);
            if (selectedFamilyInstanceList.Count != 0)
            {
                parameterSet = _doc.GetElement(selectedFamilyInstanceList.First()).Parameters;

                List<Parameter> tmpParametersList = new List<Parameter>();
                foreach (Parameter param in parameterSet)
                {
                    tmpParametersList.Add(param);
                }

                FamilyInstanceParametersCollection =
                    new ObservableCollection<Parameter>(tmpParametersList.OrderBy(p => p.Definition.Name).ToList());

                comboBox_SheetFormatParameter.ItemsSource = FamilyInstanceParametersCollection;
                comboBox_SheetFormatParameter.DisplayMemberPath = "Definition.Name";

                if (comboBox_SheetFormatParameter.Items.Count > 0)
                    comboBox_SheetFormatParameter.SelectedItem = comboBox_SheetFormatParameter.Items.GetItemAt(0);
            }
            else
            {
                TaskDialog.Show("Revit",
                    "Если размер формата листа настраивается через параметр экземпляра, " +
                    "перед запуском плагина необходимо вручную создать в проекте один лист, " +
                    "который планируется использовать как первый лист спецификации. " +
                    "Это необходимо для заполнения выпадающего списка \"Параметр формата листа\".");
            }
        }

        #endregion

        #region Радиокнопки

        private void radioButton_Checked(object sender, RoutedEventArgs e)
        {
            string selectedButtonName = (this.groupBox_SheetSize.Content as Grid)
                .Children.OfType<RadioButton>()
                .FirstOrDefault(rb => rb.IsChecked == true)
                ?.Name;

            if (selectedButtonName == "radioButton_Instance")
            {
                if (label_SheetFormatParameter != null)
                {
                    label_SheetFormatParameter.IsEnabled = true;
                    comboBox_SheetFormatParameter.IsEnabled = true;
                }
            }
            else if (selectedButtonName == "radioButton_Type")
            {
                if (label_SheetFormatParameter != null)
                {
                    label_SheetFormatParameter.IsEnabled = false;
                    comboBox_SheetFormatParameter.IsEnabled = false;
                }
            }
        }

        private void radioButton_HeaderInSpecificationHeader_Checked(object sender, RoutedEventArgs e)
        {
            string selectedButtonName = (this.groupBox_HeaderInSpecificationHeader.Content as Grid)
                .Children.OfType<RadioButton>()
                .FirstOrDefault(rb => rb.IsChecked == true)
                ?.Name;

            if (selectedButtonName == "radioButton_Yes")
            {
                if (label_SpecificationHeaderHeight != null)
                {
                    label_SpecificationHeaderHeight.IsEnabled = false;
                    textBox_SpecificationHeaderHeight.IsEnabled = false;
                }
            }
            else if (selectedButtonName == "radioButton_No")
            {
                if (label_SpecificationHeaderHeight != null)
                {
                    label_SpecificationHeaderHeight.IsEnabled = true;
                    textBox_SpecificationHeaderHeight.IsEnabled = true;
                }
            }
        }

        #endregion

        #region ОК / Отмена / клавиатура

        private void btn_Ok_Click(object sender, RoutedEventArgs e)
        {
            SaveDialogResultValues();
            DialogResult = true;
            Close();
        }

        private void btn_Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ViewScheduleSheetSpreaderWPF_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Space)
            {
                SaveDialogResultValues();
                DialogResult = true;
                Close();
            }
            else if (e.Key == Key.Escape)
            {
                DialogResult = false;
                Close();
            }
        }

        #endregion

        #region Поиск по спецификациям

        private void textBox_SearchViewSchedule_TextChanged(object sender, TextChangedEventArgs e)
        {
            _searchText = textBox_SearchViewSchedule.Text ?? string.Empty;
            RebuildAvailableViewSchedules();
        }

        /// <summary>
        /// Пересобирает список "Спецификации в проекте":
        /// - исключает уже выбранные,
        /// - применяет поиск,
        /// - сортирует по имени.
        /// </summary>
        private void RebuildAvailableViewSchedules()
        {
            IEnumerable<ViewSchedule> available = _allViewSchedules
                .Where(vs => vs != null && !SelectedViewScheduleCollection.Contains(vs));

            if (!string.IsNullOrWhiteSpace(_searchText))
            {
                available = available.Where(vs =>
                    !string.IsNullOrEmpty(vs.Name) &&
                    vs.Name.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            var ordered = available
                .OrderBy(vs => vs.Name, _nameComparer)
                .ToList();

            ViewScheduleInProjectCollection.Clear();
            foreach (var vs in ordered)
            {
                ViewScheduleInProjectCollection.Add(vs);
            }
        }

        private void listBox_SelectedViewScheduleCollection_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);
        }

        private void listBox_SelectedViewScheduleCollection_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
                return;

            Point mousePos = e.GetPosition(null);
            Vector diff = _dragStartPoint - mousePos;

            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                var listBox = sender as ListBox;
                if (listBox == null)
                    return;

                // Элемент под курсором
                var listBoxItem = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource);
                if (listBoxItem == null)
                    return;

                var draggedItem = listBox.ItemContainerGenerator.ItemFromContainer(listBoxItem) as ViewSchedule;
                if (draggedItem == null)
                    return;

                DragDrop.DoDragDrop(listBoxItem, draggedItem, DragDropEffects.Move);
            }
        }
        private void listBox_SelectedViewScheduleCollection_DragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(ViewSchedule)))
            {
                e.Effects = DragDropEffects.None;
            }
            else
            {
                e.Effects = DragDropEffects.Move;
            }

            e.Handled = true;
        }
        private void listBox_SelectedViewScheduleCollection_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(ViewSchedule)))
                return;

            var listBox = sender as ListBox;
            if (listBox == null)
                return;

            var droppedItem = e.Data.GetData(typeof(ViewSchedule)) as ViewSchedule;
            if (droppedItem == null)
                return;

            // Цель — элемент, над которым бросили
            var targetItem = GetItemFromPoint(listBox, e.GetPosition(listBox));

            // Если бросили "в никуда" (под списком) — отправляем в конец
            if (targetItem == null)
            {
                int oldIndexEnd = SelectedViewScheduleCollection.IndexOf(droppedItem);
                if (oldIndexEnd >= 0)
                {
                    SelectedViewScheduleCollection.RemoveAt(oldIndexEnd);
                    SelectedViewScheduleCollection.Add(droppedItem);
                    listBox.SelectedItem = droppedItem;
                }
                return;
            }

            if (ReferenceEquals(droppedItem, targetItem))
                return;

            int oldIndex = SelectedViewScheduleCollection.IndexOf(droppedItem);
            int newIndex = SelectedViewScheduleCollection.IndexOf(targetItem);

            if (oldIndex < 0 || newIndex < 0)
                return;

            // Классический "Move" с учётом сдвига индексов
            if (oldIndex < newIndex)
            {
                SelectedViewScheduleCollection.Insert(newIndex + 1, droppedItem);
                SelectedViewScheduleCollection.RemoveAt(oldIndex);
            }
            else
            {
                int removeIndex = oldIndex + 1;
                SelectedViewScheduleCollection.Insert(newIndex, droppedItem);
                SelectedViewScheduleCollection.RemoveAt(removeIndex);
            }

            listBox.SelectedItem = droppedItem;
        }
        private static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T)
                    return (T)current;

                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private ViewSchedule GetItemFromPoint(ListBox listBox, Point point)
        {
            var element = listBox.InputHitTest(point) as DependencyObject;
            if (element == null)
                return null;

            var listBoxItem = FindAncestor<ListBoxItem>(element);
            if (listBoxItem == null)
                return null;

            return listBox.ItemContainerGenerator.ItemFromContainer(listBoxItem) as ViewSchedule;
        }
        #endregion

        #region Сохранение / загрузка настроек

        private void SaveDialogResultValues()
        {
            ViewScheduleSheetSpreaderSettingsItem = new ViewScheduleSheetSpreaderSettings();

            // Рамки и типы
            var firstSheetFamily = comboBox_FirstSheetFamily.SelectedItem as Family;
            if (firstSheetFamily != null)
                ViewScheduleSheetSpreaderSettingsItem.FirstSheetFamilyName = firstSheetFamily.Name;

            FirstSheetType = comboBox_FirstSheetType.SelectedItem as FamilySymbol;
            if (FirstSheetType != null)
                ViewScheduleSheetSpreaderSettingsItem.FirstSheetTypeName = FirstSheetType.Name;

            var followingSheetFamily = comboBox_FollowingSheetsFamily.SelectedItem as Family;
            if (followingSheetFamily != null)
                ViewScheduleSheetSpreaderSettingsItem.FollowingSheetsFamilyName = followingSheetFamily.Name;

            FollowingSheetType = comboBox_FollowingSheetsType.SelectedItem as FamilySymbol;
            if (FollowingSheetType != null)
                ViewScheduleSheetSpreaderSettingsItem.FollowingSheetsTypeName = FollowingSheetType.Name;

            SheetFormatParameter = comboBox_SheetFormatParameter.SelectedItem as Parameter;
            if (SheetFormatParameter != null)
                ViewScheduleSheetSpreaderSettingsItem.SheetFormatParameterName = SheetFormatParameter.Definition.Name;

            // X/Y больше не задаём с формы — по рамке
            XOffset = 0;
            YOffset = 0;
            ViewScheduleSheetSpreaderSettingsItem.XOffsetValue = "0";
            ViewScheduleSheetSpreaderSettingsItem.YOffsetValue = "0";

            // Номер первого листа
            int.TryParse(textBox_FirstSheetNumber.Text, out FirstSheetNumber);
            ViewScheduleSheetSpreaderSettingsItem.FirstSheetNumberValue = textBox_FirstSheetNumber.Text;

            // Радиокнопки
            SheetSizeVariantName = (this.groupBox_SheetSize.Content as Grid)
                .Children.OfType<RadioButton>()
                .FirstOrDefault(rb => rb.IsChecked == true)
                ?.Name;
            ViewScheduleSheetSpreaderSettingsItem.SheetSizeSelectedButtonName = SheetSizeVariantName;

            HeaderInSpecificationHeaderVariantName = (this.groupBox_HeaderInSpecificationHeader.Content as Grid)
                .Children.OfType<RadioButton>()
                .FirstOrDefault(rb => rb.IsChecked == true)
                ?.Name;
            ViewScheduleSheetSpreaderSettingsItem.HeaderInSpecificationHeaderSelectedButtonName = HeaderInSpecificationHeaderVariantName;

            // Суффикс номера
            SheetNumberSuffix = textBox_SheetNumberSuffix.Text;
            ViewScheduleSheetSpreaderSettingsItem.SheetNumberSuffix = SheetNumberSuffix;

            // Высота шапки
            double.TryParse(textBox_SpecificationHeaderHeight.Text, out SpecificationHeaderHeight);
            ViewScheduleSheetSpreaderSettingsItem.SpecificationHeaderHeightValue = textBox_SpecificationHeaderHeight.Text;

            // Сохраняем настройки
            ViewScheduleSheetSpreaderSettingsItem.SaveSettings();

            // Сохраняем выбранные спецификации в xml
            List<string> selectedViewScheduleList = new List<string>();
            foreach (ViewSchedule viewSchedule in listBox_SelectedViewScheduleCollection.Items)
            {
                selectedViewScheduleList.Add(viewSchedule.Name);
            }

            if (selectedViewScheduleList.Count != 0)
            {
                new ViewScheduleListToXML().SaveList(selectedViewScheduleList);
            }
        }

        private void SetSavedSettingsValueToForm()
        {
            // Радиокнопки размера листа
            if (ViewScheduleSheetSpreaderSettingsItem.SheetSizeSelectedButtonName != null)
            {
                if (ViewScheduleSheetSpreaderSettingsItem.SheetSizeSelectedButtonName == "radioButton_Type")
                    radioButton_Type.IsChecked = true;
                else
                    radioButton_Instance.IsChecked = true;
            }

            // Семейства рамок
            if (TitleBlocksForFirstSheetCollection.FirstOrDefault(tb => tb.Name == ViewScheduleSheetSpreaderSettingsItem.FirstSheetFamilyName) != null)
                comboBox_FirstSheetFamily.SelectedItem = TitleBlocksForFirstSheetCollection.First(tb => tb.Name == ViewScheduleSheetSpreaderSettingsItem.FirstSheetFamilyName);
            else if (comboBox_FirstSheetFamily.Items.Count > 0)
                comboBox_FirstSheetFamily.SelectedItem = comboBox_FirstSheetFamily.Items.GetItemAt(0);

            if (TitleBlocksForFollowingSheetsCollection.FirstOrDefault(tb => tb.Name == ViewScheduleSheetSpreaderSettingsItem.FollowingSheetsFamilyName) != null)
                comboBox_FollowingSheetsFamily.SelectedItem = TitleBlocksForFollowingSheetsCollection.First(tb => tb.Name == ViewScheduleSheetSpreaderSettingsItem.FollowingSheetsFamilyName);
            else if (comboBox_FollowingSheetsFamily.Items.Count > 0)
                comboBox_FollowingSheetsFamily.SelectedItem = comboBox_FollowingSheetsFamily.Items.GetItemAt(0);

            // Типы рамок (после того как отработали SelectionChanged у семейства)
            if (TitleBlocksForFirstSheetTypeCollection != null &&
                TitleBlocksForFirstSheetTypeCollection.FirstOrDefault(tb => tb.Name == ViewScheduleSheetSpreaderSettingsItem.FirstSheetTypeName) != null)
                comboBox_FirstSheetType.SelectedItem = TitleBlocksForFirstSheetTypeCollection.First(tb => tb.Name == ViewScheduleSheetSpreaderSettingsItem.FirstSheetTypeName);
            else if (comboBox_FirstSheetType.Items.Count > 0)
                comboBox_FirstSheetType.SelectedItem = comboBox_FirstSheetType.Items.GetItemAt(0);

            if (TitleBlocksForFollowingSheetsTypeCollection != null &&
                TitleBlocksForFollowingSheetsTypeCollection.FirstOrDefault(tb => tb.Name == ViewScheduleSheetSpreaderSettingsItem.FollowingSheetsTypeName) != null)
                comboBox_FollowingSheetsType.SelectedItem = TitleBlocksForFollowingSheetsTypeCollection.First(tb => tb.Name == ViewScheduleSheetSpreaderSettingsItem.FollowingSheetsTypeName);
            else if (comboBox_FollowingSheetsType.Items.Count > 0)
                comboBox_FollowingSheetsType.SelectedItem = comboBox_FollowingSheetsType.Items.GetItemAt(0);

            // Параметр формата листа
            if (FamilyInstanceParametersCollection != null &&
                FamilyInstanceParametersCollection.FirstOrDefault(p => p.Definition.Name == ViewScheduleSheetSpreaderSettingsItem.SheetFormatParameterName) != null)
            {
                comboBox_SheetFormatParameter.SelectedItem =
                    FamilyInstanceParametersCollection.First(p => p.Definition.Name == ViewScheduleSheetSpreaderSettingsItem.SheetFormatParameterName);
            }
            else if (comboBox_SheetFormatParameter.Items.Count != 0)
            {
                comboBox_SheetFormatParameter.SelectedItem = comboBox_SheetFormatParameter.Items.GetItemAt(0);
            }

            // Номер первого листа
            if (!string.IsNullOrEmpty(ViewScheduleSheetSpreaderSettingsItem.FirstSheetNumberValue))
                textBox_FirstSheetNumber.Text = ViewScheduleSheetSpreaderSettingsItem.FirstSheetNumberValue;
            else
                textBox_FirstSheetNumber.Text = "69";

            // Шапка в заголовке спецификации
            if (ViewScheduleSheetSpreaderSettingsItem.HeaderInSpecificationHeaderSelectedButtonName != null)
            {
                if (ViewScheduleSheetSpreaderSettingsItem.HeaderInSpecificationHeaderSelectedButtonName == "radioButton_No")
                {
                    radioButton_No.IsChecked = true;
                    if (label_SpecificationHeaderHeight != null)
                    {
                        label_SpecificationHeaderHeight.IsEnabled = true;
                        textBox_SpecificationHeaderHeight.IsEnabled = true;
                    }
                }
                else
                {
                    radioButton_Yes.IsChecked = true;
                    if (label_SpecificationHeaderHeight != null)
                    {
                        label_SpecificationHeaderHeight.IsEnabled = false;
                        textBox_SpecificationHeaderHeight.IsEnabled = false;
                    }
                }
            }

            // Высота шапки
            if (!string.IsNullOrEmpty(ViewScheduleSheetSpreaderSettingsItem.SpecificationHeaderHeightValue))
                textBox_SpecificationHeaderHeight.Text = ViewScheduleSheetSpreaderSettingsItem.SpecificationHeaderHeightValue;
            else
                textBox_SpecificationHeaderHeight.Text = "40";

            // Суффикс номера
            if (ViewScheduleSheetSpreaderSettingsItem.SheetNumberSuffix != null)
                textBox_SheetNumberSuffix.Text = ViewScheduleSheetSpreaderSettingsItem.SheetNumberSuffix;
            else
                textBox_SheetNumberSuffix.Text = "";

            // Восстановление списка выбранных спецификаций
            if (SelectedViewScheduleList != null)
            {
                foreach (string selectedViewScheduleName in SelectedViewScheduleList)
                {
                    var vs = _allViewSchedules.FirstOrDefault(v => v.Name == selectedViewScheduleName);
                    if (vs != null && !SelectedViewScheduleCollection.Contains(vs))
                    {
                        SelectedViewScheduleCollection.Add(vs);
                    }
                }
            }
        }

        #endregion
    }
}
