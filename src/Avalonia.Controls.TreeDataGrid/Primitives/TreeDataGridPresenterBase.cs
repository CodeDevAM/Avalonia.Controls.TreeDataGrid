﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Controls.Presenters;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace Avalonia.Controls.Primitives
{
    public abstract class TreeDataGridPresenterBase<TItem> : Control, IPanel, IPresenter
    {
        public static readonly DirectProperty<TreeDataGridPresenterBase<TItem>, IElementFactory?> ElementFactoryProperty =
            AvaloniaProperty.RegisterDirect<TreeDataGridPresenterBase<TItem>, IElementFactory?>(
                nameof(ElementFactory),
                o => o.ElementFactory,
                (o, v) => o.ElementFactory = v);

        public static DirectProperty<TreeDataGridPresenterBase<TItem>, IReadOnlyList<TItem>?> ItemsProperty =
            AvaloniaProperty.RegisterDirect<TreeDataGridPresenterBase<TItem>, IReadOnlyList<TItem>?>(
                nameof(Items),
                o => o.Items,
                (o, v) => o.Items = v);

        private static readonly Rect s_invalidViewport = new Rect(double.PositiveInfinity, double.PositiveInfinity, 0, 0);
        private int _anchorIndex = -1;
        private IControl? _anchorElement;
        private readonly Controls _children = new Controls();
        private IElementFactory? _elementFactory;
        private bool _isWaitingForViewportUpdate;
        private IReadOnlyList<TItem>? _items;
        private RealizedElementList _measureElements = new RealizedElementList();
        private RealizedElementList _realizedElements = new RealizedElementList();

        public TreeDataGridPresenterBase()
        {
            _children.CollectionChanged += OnChildrenChanged;
            EffectiveViewportChanged += OnEffectiveViewportChanged;
        }

        public IElementFactory? ElementFactory
        {
            get => _elementFactory;
            set => SetAndRaise(ElementFactoryProperty, ref _elementFactory, value);
        }

        public IReadOnlyList<TItem>? Items 
        {
            get => _items;
            set
            {
                if (_items != value)
                {
                    if (_items is INotifyCollectionChanged oldIncc)
                        oldIncc.CollectionChanged -= OnItemsCollectionChanged;

                    var oldValue = _items;
                    _items = value;

                    if (_items is INotifyCollectionChanged newIncc)
                        newIncc.CollectionChanged += OnItemsCollectionChanged;

                    RaisePropertyChanged(
                        ItemsProperty,
                        new Optional<IReadOnlyList<TItem>?>(oldValue),
                        new BindingValue<IReadOnlyList<TItem>?>(_items));
                }
            }
        }

        Controls IPanel.Children => _children;

        protected abstract Orientation Orientation { get; }
        protected Rect Viewport { get; private set; } = s_invalidViewport;

        internal IEnumerable<IControl?> RealizedElements => _realizedElements.Elements;

        public void BringIntoView(int index)
        {
            if (GetRealizedElement(index) is IControl element)
            {
                element.BringIntoView();
            }
            else if (this.GetVisualRoot() is ILayoutRoot root)
            {
                // Create and measure the element to be brought into view. Store it in a field so that
                // it can be re-used in the layout pass.
                _anchorElement = GetOrCreateElement(index);
                _anchorElement.Measure(Size.Infinity);
                _anchorIndex = index;

                // Get the expected position of the elment and put it in place.
                var anchorU = GetOrEstimateElementPosition(index);
                var rect = Orientation == Orientation.Horizontal ?
                    new Rect(anchorU, 0, _anchorElement.DesiredSize.Width, _anchorElement.DesiredSize.Height) :
                    new Rect(0, anchorU, _anchorElement.DesiredSize.Width, _anchorElement.DesiredSize.Height);
                _anchorElement.Arrange(rect);

                // Try to bring the item into view and do a layout pass.
                _anchorElement.BringIntoView();

                _isWaitingForViewportUpdate = true;
                root.LayoutManager.ExecuteLayoutPass();
                _isWaitingForViewportUpdate = false;

                _anchorElement = null;
                _anchorIndex = -1;
            }
        }

        public IControl? TryGetElement(int index) => GetRealizedElement(index);

        protected virtual Rect ArrangeElement(int index, IControl element, Rect rect)
        {
            element.Arrange(rect);
            return rect;
        }

        protected virtual Size MeasureElement(int index, IControl element, Size availableSize)
        {
            element.Measure(availableSize);
            return element.DesiredSize;
        }

        protected virtual IControl GetElementFromFactory(TItem item, int index)
        {
            return _elementFactory!.GetElement(new ElementFactoryGetArgs
            {
                Data = item,
                Index = index,
                Parent = this,
            });
        }

        protected virtual (int index, double position) GetElementAt(double position) => (-1, -1);
        protected virtual double GetElementPosition(int index) => -1;
        protected abstract void Realize(IControl element, TItem item, int index);
        protected abstract void UpdateIndex(IControl element, int index);
        protected abstract void Unrealize(IControl element);

        protected override Size MeasureOverride(Size availableSize)
        {
            if (Items is null || Items.Count == 0 || !IsEffectivelyVisible)
                return default;

            // If we're bringing an item into view, ignore any layout passes until we receive a new
            // effective viewport.
            if (_isWaitingForViewportUpdate)
                return DesiredSize;

            // We handle horizontal and vertical layouts here so X and Y are abstracted to:
            // - Horizontal layouts: U = horizontal, V = vertical
            // - Vertical layouts: U = vertical, V = horizontal
            var viewport = CalculateMeasureViewport();

            // Recycle elements that we expect to be outside the viewport. Doing this before the
            // measure means we don't have to create more elements than necessary.
            if (IntersectsRealizedRange(viewport.firstIndex, viewport.estimatedLastIndex))
                RecycleElementsBefore(viewport.firstIndex);
            else
                RecycleAllElements();

            // Do the measure, creating/recycling elements as necessary to fill the viewport. Don't
            // write to _realizedElements yet, only _measureElements.
            GenerateElements(availableSize, ref viewport);

            // Now we know what fits, recycle anything left over.
            RecycleElementsAfter(_measureElements.LastIndex);

            // And swap the measureElements and realizedElements collection.
            var tmp = _realizedElements;
            _realizedElements = _measureElements;
            _measureElements = tmp;
            _measureElements.Clear();

            // Return the estimated size of all items based on the elements currently realized.
            var estimatedSize = EstimateElementSizeU() * Items.Count;

            // Clear the anchor.
            _anchorIndex = -1;

            return Orientation == Orientation.Horizontal ?
                new Size(estimatedSize, viewport.measuredV) :
                new Size(viewport.measuredV, estimatedSize);
        }

        private void GenerateElements(Size availableSize, ref MeasureViewport viewport)
        {
            _ = Items ?? throw new AvaloniaInternalException("Items may not be null.");

            var horizontal = Orientation == Orientation.Horizontal;
            var index = viewport.firstIndex;
            var u = viewport.startU;

            do
            {
                var e = GetOrCreateElement(index);
                var slot = MeasureElement(index, e, availableSize);
                var sizeU = horizontal ? slot.Width : slot.Height;

                _measureElements.Add(index, e, u, sizeU);
                viewport.measuredV = Math.Max(viewport.measuredV, horizontal ? slot.Height : slot.Width);

                u += sizeU;
                ++index;
            } while (u < viewport.viewportUEnd && index < Items.Count);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var orientation = Orientation;
            var u = _realizedElements.StartU;

            for (var i = 0; i < _realizedElements.Count; ++i)
            {
                var e = _realizedElements.Elements[i];

                if (e is object)
                {
                    var sizeU = _realizedElements.SizeU[i];
                    var rect = orientation == Orientation.Horizontal ?
                        new Rect(u, 0, sizeU, finalSize.Height) :
                        new Rect(0, u, finalSize.Width, sizeU);
                    rect = ArrangeElement(i, e, rect);
                    u += orientation == Orientation.Horizontal ? rect.Width : rect.Height;
                }
            }

            return finalSize;
        }

        protected void RecycleAllElements()
        {
            foreach (var e in _realizedElements.Elements)
            {
                if (e is object)
                    RecycleElement(e);
            }

            _realizedElements.Clear();
        }

        protected override void OnDetachedFromLogicalTree(LogicalTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromLogicalTree(e);
            RecycleAllElements();
        }

        protected virtual void OnEffectiveViewportChanged(object sender, EffectiveViewportChangedEventArgs e)
        {
            Viewport = e.EffectiveViewport;
            _isWaitingForViewportUpdate = false;
            InvalidateMeasure();
        }

        private MeasureViewport CalculateMeasureViewport()
        {
            // If the control has not yet been laid out then the effective viewport won't have been set.
            // Try to work it out from an ancestor control.
            var viewport = Viewport != s_invalidViewport ? Viewport : EstimateViewport();

            // Get the viewport in the orientation direction.
            var viewportStart = Orientation == Orientation.Horizontal ? viewport.X : viewport.Y;
            var viewportEnd = Orientation == Orientation.Horizontal ? viewport.Right : viewport.Bottom;

            var (firstIndex, firstIndexU) = GetElementAt(viewportStart);
            var (lastIndex, _) = GetElementAt(viewportEnd);
            var estimatedElementSize = -1.0;

            if (firstIndex == -1)
            {
                estimatedElementSize = EstimateElementSizeU();
                firstIndex = Math.Min((int)(viewportStart / estimatedElementSize), Items!.Count - 1);
                firstIndexU = firstIndex * estimatedElementSize;
            }

            if (lastIndex == -1)
            {
                if (estimatedElementSize == -1)
                    estimatedElementSize = EstimateElementSizeU();
                lastIndex = (int)(viewportEnd / estimatedElementSize);
            }

            return new MeasureViewport
            {
                firstIndex = firstIndex,
                estimatedLastIndex = lastIndex,
                viewportUStart = viewportStart,
                viewportUEnd = viewportEnd,
                startU = firstIndexU,
            };
        }

        private IControl GetOrCreateElement(int index)
        {
            var e = GetRealizedElement(index) ?? GetRecycledOrCreateElement(index);
            InvalidateHack(e);
            return e;
        }

        private double GetOrEstimateElementPosition(int index)
        {
            var u = GetElementPosition(index);

            if (u >= 0)
                return u;

            var estimatedElementSize = EstimateElementSizeU();
            return index * estimatedElementSize;
        }

        private IControl? GetRealizedElement(int index)
        {
            if (_anchorIndex == index)
                return _anchorElement;
            return index >= _realizedElements.FirstIndex && index <= _realizedElements.LastIndex ?
                _realizedElements.Elements[index - _realizedElements.FirstIndex] : null;
        }

        private IControl GetRecycledOrCreateElement(int index)
        {
            var item = Items![index];
            var e = GetElementFromFactory(item, index);
            e.IsVisible = true;
            Realize(e, item, index);
            if (e.Parent is null)
                _children.Add(e);
            return e;
        }

        private double EstimateElementSizeU()
        {
            var count = _realizedElements.Count;
            var total = 0.0;

            for (var i = 0; i < count; ++i)
            {
                if (_realizedElements.Elements[i] is object)
                    total += _realizedElements.SizeU[i];
                else
                    --count;
            }

            return count > 0 ? total / count : 25;
        }

        private Rect EstimateViewport()
        {
            var c = this.GetVisualParent();
            var viewport = new Rect();

            while (c is object)
            {
                if (!c.Bounds.IsEmpty && c?.TransformToVisual(this) is Matrix transform)
                {
                    viewport = new Rect(0, 0, c.Bounds.Width, c.Bounds.Height)
                        .TransformToAABB(transform);
                    break;
                }

                c = c.GetVisualParent();
            }


            return viewport;
        }

        private bool IntersectsRealizedRange(int firstIndex, double lastIndex)
        {
            if (_realizedElements.Count == 0)
                return false;

            var first = _realizedElements.FirstIndex;
            var last = _realizedElements.LastIndex;

            return (firstIndex >= first && firstIndex <= last) ||
                   (lastIndex > first && lastIndex <= last);
        }

        private void RecycleElement(IControl element)
        {
            Unrealize(element);
            element.IsVisible = false;
            ElementFactory!.RecycleElement(new ElementFactoryRecycleArgs
            {
                Element = element,
                Parent = this,
            });
        }

        private void RecycleElementsAfter(int index)
        {
            var first = _realizedElements.FirstIndex;
            var last = _realizedElements.LastIndex;

            for (var i = last; i > index && i >= first; --i)
            {
                if (_realizedElements.Elements[i - first] is IControl e)
                    RecycleElement(e);
            }

            // No need to adjust the _realizedElements collection here as this will be called at the
            // end of the measure process.
        }

        private void RecycleElementsBefore(int index)
        {
            var first = _realizedElements.FirstIndex;

            if (index < first)
                return;

            var count = index - first;

            for (var i = 0; i < count && i < _realizedElements.Count; ++i)
            {
                if (_realizedElements.Elements[i] is IControl e)
                    RecycleElement(e);
            }

            _realizedElements.RemoveRange(0, count);
        }

        private void OnChildrenChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            void Add(IList items)
            {
                foreach (var i in items)
                {
                    if (i is IControl c)
                    {
                        LogicalChildren.Add(c);
                        VisualChildren.Add(c);
                    }
                }
            }

            void Remove(IList items)
            {
                foreach (var i in items)
                {
                    if (i is IControl c)
                    {
                        LogicalChildren.Remove(c);
                        VisualChildren.Remove(c);
                    }
                }
            }

            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    Add(e.NewItems);
                    break;

                case NotifyCollectionChangedAction.Remove:
                    Remove(e.OldItems);
                    break;

                case NotifyCollectionChangedAction.Replace:
                    Remove(e.OldItems);
                    Add(e.NewItems);
                    break;

                case NotifyCollectionChangedAction.Reset:
                    throw new NotSupportedException();
            }
        }

        private void OnItemsCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            void Add(int index, int count)
            {
                var first = _realizedElements.FirstIndex;
                var last = _realizedElements.LastIndex;

                if (index >= first && index <= last)
                {
                    var insertPoint = index - first;

                    for (var i = insertPoint; i < _realizedElements.Count; ++i)
                    {
                        if (_realizedElements.Elements[i] is IControl element)
                            UpdateIndex(element, first + i + count);
                    }

                    _realizedElements.InsertSpace(insertPoint, count);
                }
            }

            void Remove(int index, int count)
            {
                var first = _realizedElements.FirstIndex;
                var last = _realizedElements.LastIndex;

                if (index >= first && index <= last)
                {
                    var removePoint = index - first;
                    count = Math.Min(count, _realizedElements.Count - removePoint);

                    for (var i = 0; i < count; ++i)
                    {
                        if (_realizedElements.Elements[removePoint + i] is IControl element)
                            RecycleElement(element);
                    }

                    _realizedElements.RemoveRange(removePoint, count);

                    for (var i = removePoint; i < _realizedElements.Count; ++i)
                    {
                        if (_realizedElements.Elements[i] is IControl element)
                            UpdateIndex(element, first + i);
                    }
                }
            }

            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    Add(e.NewStartingIndex, e.NewItems.Count);
                    break;
                case NotifyCollectionChangedAction.Remove:
                    Remove(e.OldStartingIndex, e.OldItems.Count);
                    break;
                case NotifyCollectionChangedAction.Reset:
                    RecycleAllElements();
                    break;
            }

            InvalidateMeasure();
        }

        private static void InvalidateHack(IControl c)
        {
            bool HasInvalidations(IControl c)
            {
                if (!c.IsMeasureValid)
                    return true;

                for (var i = 0; i < c.VisualChildren.Count; ++i)
                {
                    if (c.VisualChildren[i] is IControl child)
                    {
                        if (!child.IsMeasureValid || HasInvalidations(child))
                            return true;
                    }
                }

                return false;
            }

            void Invalidate(IControl c)
            {
                c.InvalidateMeasure();
                for (var i = 0; i < c.VisualChildren.Count; ++i)
                {
                    if (c.VisualChildren[i] is IControl child)
                        Invalidate(child);
                }
            }

            if (HasInvalidations(c))
                Invalidate(c);
        }

        private struct RealizedElementList
        {
            private int _firstIndex;
            private List<IControl?>? _elements;
            private List<double>? _sizes;
            private double _startU;

            public int Count => _elements?.Count ?? 0;
            public int FirstIndex => _elements?.Count > 0 ? _firstIndex : -1;
            public int LastIndex => _elements?.Count > 0 ? _firstIndex + _elements.Count - 1 : -1;
            public IReadOnlyList<IControl?> Elements => _elements ??= new List<IControl?>();
            public IReadOnlyList<double> SizeU => _sizes ??= new List<double>();
            public double StartU => _startU;

            public void Add(int index, IControl element, double u, double sizeU)
            {
                if (index < 0)
                    throw new ArgumentOutOfRangeException(nameof(index));

                _elements ??= new List<IControl?>();
                _sizes ??= new List<double>();

                if (Count == 0)
                {
                    _elements.Add(element);
                    _sizes.Add(sizeU);
                    _startU = u;
                    _firstIndex = index;
                }
                else if (index == LastIndex + 1)
                {
                    _elements.Add(element);
                    _sizes.Add(sizeU);
                }
                else if (index == FirstIndex - 1)
                {
                    --_firstIndex;
                    _elements.Insert(0, element);
                    _sizes.Insert(0, sizeU);
                    _startU = u;
                }
                else
                {
                    throw new NotSupportedException("Can only add items to the beginning or end of realized elements.");
                }
            }

            public void InsertSpace(int index, int count)
            {
                if (Count == 0)
                    return;

                _elements!.InsertMany(index, null, count);
                _sizes!.InsertMany(index, 0.0, count);
            }

            public void RemoveRange(int index, int count)
            {
                _elements?.RemoveRange(index, count);
                _sizes?.RemoveRange(index, count);

                if (index == 0)
                    _firstIndex += count;
            }

            public void Clear()
            {
                _startU = _firstIndex = 0;
                _elements?.Clear();
                _sizes?.Clear();
            }
        }

        private struct MeasureViewport
        {
            public int firstIndex;
            public int estimatedLastIndex;
            public double viewportUStart;
            public double viewportUEnd;
            public double measuredV;
            public double startU;
        }
    }
}
