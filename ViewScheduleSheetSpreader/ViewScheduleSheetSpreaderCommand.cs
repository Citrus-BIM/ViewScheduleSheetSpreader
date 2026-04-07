using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace ViewScheduleSheetSpreader
{
    [Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
    class ViewScheduleSheetSpreaderCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try { _ = GetPluginStartInfo(); } catch { }

            Document doc = commandData.Application.ActiveUIDocument.Document;
            int.TryParse(commandData.Application.Application.VersionNumber, out int versionNumber);

            if (versionNumber < 2023)
            {
                TaskDialog.Show("Revit", "К сожалению, возможность разделения спецификаций по листам доступна только с версии Revit 2023 и выше!");
                return Result.Cancelled;
            }

#if !R2019 && !R2020 && !R2021 && !R2022
            // 1) Сбор исходных данных
            var viewScheduleList = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Schedules)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Where(vs => !vs.IsTitleblockRevisionSchedule)
                .OrderBy(vs => vs.Name, new AlphanumComparatorFastString())
                .ToList();

            var titleBlockFamilysList = new FilteredElementCollector(doc)
                .OfClass(typeof(Family))
                .Cast<Family>()
#if R2019 || R2020 || R2021 || R2022 || R2023 || R2024 || R2025
                .Where(f => f.FamilyCategory.Id.IntegerValue.Equals((int)BuiltInCategory.OST_TitleBlocks))
#else
                .Where(f => f.FamilyCategory.Id.Value == new ElementId(BuiltInCategory.OST_TitleBlocks).Value)
#endif
                .OrderBy(f => f.Name, new AlphanumComparatorFastString())
                .ToList();

            var wpf = new ViewScheduleSheetSpreaderWPF(doc, viewScheduleList, titleBlockFamilysList);
            wpf.ShowDialog();
            if (wpf.DialogResult != true)
            {
                return Result.Cancelled;
            }

            // 2) Параметры с формы
            var selectedViewScheduleList = wpf.SelectedViewScheduleCollection.ToList();
            if (!selectedViewScheduleList.Any())
            {
                return Result.Cancelled;
            }

            FamilySymbol firstSheetType = wpf.FirstSheetType;
            FamilySymbol followingSheetType = wpf.FollowingSheetType;
            Parameter sheetFormatParameter = wpf.SheetFormatParameter;
            string sheetSizeVariantName = wpf.SheetSizeVariantName;
            int firstSheetNumber = wpf.FirstSheetNumber;
            string headerVariant = wpf.HeaderInSpecificationHeaderVariantName; // "radioButton_No" -> шапка на листе
            double specHeaderHeightFeet = wpf.SpecificationHeaderHeight / 304.8; // ft
            string sheetNumberSuffix = wpf.SheetNumberSuffix ?? string.Empty;

            const double mmToFt = 1.0 / 304.8;
            const double EPS = 0.1 * mmToFt; // ~0.1 мм

            // Максимально допустимая высота спецификации на листе (включая заголовки самой спецификации)
            double sheetCapFirstTotal = 230.0 * mmToFt;
            double sheetCapFollowingTotal = 270.0 * mmToFt;

            // Зона под шапку на листе (если она вынесена в оформление, а не в заголовок спецификации)
            double extraHeaderZone = headerVariant == "radioButton_No" ? specHeaderHeightFeet : 0.0;

            // Реально доступная зона под спецификации (от верха рабочей области листа до "низ спецификаций")
            double sheetCapFirst = sheetCapFirstTotal - extraHeaderZone;
            double sheetCapFollowing = sheetCapFollowingTotal - extraHeaderZone;

            if (sheetCapFirst <= EPS || sheetCapFollowing <= EPS)
            {
                TaskDialog.Show("Revit", "Высота шапки или настройки листа делают доступную область нулевой. Уменьшите высоту шапки или измените параметры листа.");
                return Result.Cancelled;
            }

            // Запрещаем заранее разделённые спецификации
            foreach (var vs in selectedViewScheduleList)
            {
                if (vs.IsSplit())
                {
                    TaskDialog.Show("Revit", $"Спецификация \"{vs.Name}\" уже разделена и не может быть размещена на листы!");
                    return Result.Cancelled;
                }
            }

            var titleBlockFilter = new ElementCategoryFilter(BuiltInCategory.OST_TitleBlocks);

            using (var t = new Transaction(doc, "Спецификации на листы"))
            {
                t.Start();

                ViewSheet currentSheet = null;
                int currentSheetNumber = firstSheetNumber;
                bool firstSheetCreated = false;
                double remainOnSheet = 0.0;   // сколько высоты ещё осталось на текущем листе
                XYZ basePoint = XYZ.Zero;     // базовая точка вставки на текущем листе

                // новое: состояние контента на текущем листе
                bool sheetHasContent = false;
                double lastBottomYOnSheet = 0.0;

                // Локальная функция создания листа нужного типа
                ViewSheet CreateSheet(FamilySymbol tbType, bool isFirstSheet)
                {
                    ViewSheet sheet = ViewSheet.Create(doc, tbType.Id);

                    ElementId tbId = sheet.GetDependentElements(titleBlockFilter).FirstOrDefault();
                    FamilyInstance frame = tbId != ElementId.InvalidElementId
                        ? doc.GetElement(tbId) as FamilyInstance
                        : null;

                    if (frame != null)
                    {
                        frame.get_Parameter(BuiltInParameter.SHEET_NUMBER)
                             ?.Set($"{currentSheetNumber}{sheetNumberSuffix}");
                        frame.get_Parameter(BuiltInParameter.SHEET_NAME)
                             ?.Set("Спецификация оборудования, изделий и материалов");

                        // Размер листа по экземпляру — сначала задаём формат
                        if (sheetSizeVariantName == "radioButton_Instance" && sheetFormatParameter != null)
                        {
                            frame.LookupParameter(sheetFormatParameter.Definition.Name)
                                 ?.Set(3); // как и раньше
                        }
                    }

                    // После изменения формата рамки обновляем документ,
                    // чтобы Outline и координаты листа были актуальными
                    doc.Regenerate();

                    // После актуализации читаем Outline листа и считаем базовую точку
                    basePoint = GetScheduleBaseLocation(sheet, headerVariant, specHeaderHeightFeet);

                    // Доступная высота под спецификации на новом листе
                    remainOnSheet = isFirstSheet ? sheetCapFirst : sheetCapFollowing;

                    // на новом листе пока ничего нет
                    sheetHasContent = false;
                    lastBottomYOnSheet = basePoint.Y;

                    return sheet;
                }

                foreach (var schedule in selectedViewScheduleList)
                {
                    // Высоты внутри спецификации
                    var hs = schedule.GetScheduleHeightsOnSheet();
                    double titleH = hs.TitleHeight;
                    double headerH = hs.ColumnHeaderHeight;
                    IList<double> rowHeights = hs.GetBodyRowHeights();

                    if (rowHeights == null || rowHeights.Count == 0)
                    {
                        continue;
                    }

                    double bodyTotal = rowHeights.Sum();
                    if (bodyTotal <= EPS)
                    {
                        continue;
                    }

                    double overhead = titleH + headerH; // заголовок + заголовок колонок

                    // Сколько места остаётся под ТЕЛО спецификации на текущем листе
                    double BodyLimitOnCurrentSheet()
                    {
                        double limit = Math.Max(remainOnSheet - overhead, 0.0);
                        return limit;
                    }

                    // Убедимся, что есть активный лист
                    if (currentSheet == null)
                    {
                        bool isFirst = !firstSheetCreated;
                        currentSheet = CreateSheet(isFirst ? firstSheetType : followingSheetType, isFirst);
                        firstSheetCreated = true;
                    }

                    double bodyLimitNow = BodyLimitOnCurrentSheet();

                    // Если на текущем листе нет места даже под одну строку — сразу переходим на новый
                    if (bodyLimitNow < rowHeights[0] - EPS)
                    {
                        currentSheetNumber++;
                        currentSheet = CreateSheet(followingSheetType, false);
                    }

                    // 4.1. Расчёт высот сегментов по строкам
                    var segmentBodyHeights = new List<double>();
                    int rowIndex = 0;
                    double followBodyLimit = Math.Max(sheetCapFollowing - overhead, 0.0);

                    while (rowIndex < rowHeights.Count)
                    {
                        double bodyLimit;

                        if (segmentBodyHeights.Count == 0 && BodyLimitOnCurrentSheet() > EPS)
                        {
                            // Первый сегмент — в остаток текущего листа
                            bodyLimit = BodyLimitOnCurrentSheet();
                        }
                        else
                        {
                            // Все последующие сегменты — на "чистых" последующих листах
                            bodyLimit = followBodyLimit;
                        }

                        if (bodyLimit <= EPS)
                        {
                            // На всякий случай, если что-то пошло не так с остатком — открываем новый лист
                            currentSheetNumber++;
                            currentSheet = CreateSheet(followingSheetType, false);
                            continue;
                        }

                        double segSum = 0.0;

                        // Набиваем строки в сегмент, пока влезают
                        while (rowIndex < rowHeights.Count &&
                               segSum + rowHeights[rowIndex] <= bodyLimit + EPS)
                        {
                            segSum += rowHeights[rowIndex];
                            rowIndex++;
                        }

                        // Если из-за округлений не влезла ни одна строка — всё равно забираем одну
                        if (segSum <= EPS && rowIndex < rowHeights.Count)
                        {
                            segSum = rowHeights[rowIndex];
                            rowIndex++;
                        }

                        segmentBodyHeights.Add(segSum);
                    }

                    // Дробим спецификацию на сегменты
                    if (segmentBodyHeights.Count > 0)
                        schedule.Split(segmentBodyHeights);

                    // 4.2. Размещение сегментов на листах
                    int segmentsFromRevit = schedule.GetSegmentCount();
                    int segmentCount = Math.Min(segmentsFromRevit, segmentBodyHeights.Count); // игнорируем лишний хвостовой сегмент от Revit

                    for (int seg = 0; seg < segmentCount; seg++)
                    {
                        // Для сегментов > 0 всегда создаём новый лист
                        if (seg > 0)
                        {
                            currentSheetNumber++;
                            currentSheet = CreateSheet(followingSheetType, false);
                        }

                        // точка вставки:
                        XYZ placePoint;
                        if (!sheetHasContent)
                        {
                            // первая спецификация / первый сегмент на листе
                            placePoint = basePoint;
                        }
                        else
                        {
                            // продолжаем снизу после последнего сегмента на этом листе
                            placePoint = new XYZ(basePoint.X, lastBottomYOnSheet, 0);
                        }

                        ScheduleSheetInstance inst =
                            ScheduleSheetInstance.Create(doc, currentSheet.Id, schedule.Id, placePoint, seg);

                        // Обновим документ для корректного bbox
                        doc.Regenerate();

                        // Реальная высота сегмента по bbox (учитывает группировки, заголовки и т.п.)
                        BoundingBoxXYZ bb = inst.get_BoundingBox(currentSheet);
                        double segHeightReal = 0.0;
                        if (bb != null)
                        {
                            segHeightReal = bb.Max.Y - bb.Min.Y;
                            lastBottomYOnSheet = bb.Min.Y; // низ последнего сегмента на листе
                        }

                        remainOnSheet -= segHeightReal;
                        sheetHasContent = true;
                    }
                }

                t.Commit();
            }
#endif

            return Result.Succeeded;
        }

        // --------- вспомогательные ---------
        private static XYZ GetScheduleBaseLocation(
            ViewSheet sheet,
            string headerInSpecificationHeaderVariantName,
            double specificationHeaderHeightFeet)
        {
            if (sheet == null)
                return XYZ.Zero;

            BoundingBoxUV outline = sheet.Outline;
            if (outline == null)
                return XYZ.Zero;

            const double mmToFt = 1.0 / 304.8;

            double offsetRight = 20.0 * mmToFt; // 20 мм вправо
            double offsetDown = 5.0 * mmToFt;   // 5 мм вниз

            // Если шапка вынесена на лист (radioButton_No) — опускаемся ещё на её высоту
            if (headerInSpecificationHeaderVariantName == "radioButton_No")
                offsetDown += specificationHeaderHeightFeet;

            double x = outline.Min.U + offsetRight;
            double y = outline.Max.V - offsetDown; // вниз = уменьшение Y

            return new XYZ(x, y, 0);
        }

        private static async Task GetPluginStartInfo()
        {
            Assembly thisAssembly = Assembly.GetExecutingAssembly();
            string assemblyName = "ViewScheduleSheetSpreader";
            string assemblyNameRus = "Спецификации на листы";
            string assemblyFolderPath = Path.GetDirectoryName(thisAssembly.Location);

            int lastBackslashIndex = assemblyFolderPath.LastIndexOf("\\", StringComparison.Ordinal);
            string dllPath = assemblyFolderPath.Substring(0, lastBackslashIndex + 1) + "PluginInfoCollector\\PluginInfoCollector.dll";

            Assembly assembly = Assembly.LoadFrom(dllPath);
            Type type = assembly.GetType("PluginInfoCollector.InfoCollector");
            if (type != null)
            {
                object instance = Activator.CreateInstance(type);
                var method = type.GetMethod("CollectPluginUsageAsync");
                if (method != null)
                {
                    Task task = (Task)method.Invoke(instance, new object[] { assemblyName, assemblyNameRus });
                    await task;
                }
            }
        }
    }
}
