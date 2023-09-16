﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace BlazorBindings.Core
{
    /// <summary>
    /// Represents a "shadow" item that Blazor uses to map changes into the live native UI tree.
    /// </summary>
    [DebuggerDisplay("{DebugName}")]
    internal sealed class NativeComponentAdapter : IDisposable
    {
        private static volatile int DebugInstanceCounter;

        public NativeComponentAdapter(NativeComponentRenderer renderer, IElementHandler closestPhysicalParent, IElementHandler knownTargetElement = null)
        {
            Renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
            _closestPhysicalParent = closestPhysicalParent;
            _targetElement = knownTargetElement;

            // Assign unique counter value. This *should* all be done on one thread, but just in case, make it thread-safe.
            _debugInstanceCounterValue = Interlocked.Increment(ref DebugInstanceCounter);
        }

        private readonly int _debugInstanceCounterValue;

        private string DebugName => $"[#{_debugInstanceCounterValue}] {Name}";

        public NativeComponentAdapter Parent { get; private set; }
        public List<NativeComponentAdapter> Children { get; } = new List<NativeComponentAdapter>();

        private readonly IElementHandler _closestPhysicalParent;
        private IElementHandler _targetElement;
        private IComponent _targetComponent;

        public NativeComponentRenderer Renderer { get; }

        /// <summary>
        /// Used for debugging purposes.
        /// </summary>
        public string Name { get; internal set; }

        public override string ToString()
        {
            return $"{nameof(NativeComponentAdapter)}: Name={Name ?? "<?>"}, Target={_targetElement?.GetType().Name ?? "<None>"}, #Children={Children.Count}";
        }

        internal void ApplyEdits(int componentId, ArrayBuilderSegment<RenderTreeEdit> edits, ArrayRange<RenderTreeFrame> referenceFrames, RenderBatch batch, HashSet<int> processedComponentIds)
        {
            Renderer.Dispatcher.AssertAccess();

            foreach (var edit in edits)
            {
                switch (edit.Type)
                {
                    case RenderTreeEditType.PrependFrame:
                        ApplyPrependFrame(batch, componentId, edit.SiblingIndex, referenceFrames.Array, edit.ReferenceFrameIndex, processedComponentIds);
                        break;
                    case RenderTreeEditType.RemoveFrame:
                        ApplyRemoveFrame(edit.SiblingIndex);
                        break;
                    case RenderTreeEditType.SetAttribute:
                        ApplySetAttribute(ref referenceFrames.Array[edit.ReferenceFrameIndex]);
                        break;
                    case RenderTreeEditType.RemoveAttribute:
                        // TODO: See whether siblingIndex is needed here
                        ApplyRemoveAttribute(edit.RemovedAttributeName);
                        break;
                    case RenderTreeEditType.UpdateText:
                        {
                            var frame = batch.ReferenceFrames.Array[edit.ReferenceFrameIndex];
                            if (_targetElement is IHandleChildContentText handleChildContentText)
                            {
                                handleChildContentText.HandleText(edit.SiblingIndex, frame.TextContent);
                            }
                            else if (!string.IsNullOrWhiteSpace(frame.TextContent))
                            {
                                throw new Exception("Cannot set text content on child that doesn't handle inner text content.");
                            }
                            break;
                        }
                    case RenderTreeEditType.StepIn:
                        {
                            // TODO: Need to implement this. For now it seems safe to ignore.
                            break;
                        }
                    case RenderTreeEditType.StepOut:
                        {
                            // TODO: Need to implement this. For now it seems safe to ignore.
                            break;
                        }
                    case RenderTreeEditType.UpdateMarkup:
                        {
                            var frame = batch.ReferenceFrames.Array[edit.ReferenceFrameIndex];
                            if (_targetElement is IHandleChildContentText handleChildContentText)
                            {
                                handleChildContentText.HandleText(edit.SiblingIndex, frame.MarkupContent);
                            }
                            else if (!string.IsNullOrWhiteSpace(frame.MarkupContent))
                            {
                                throw new Exception("Cannot set markup content on child that doesn't handle inner text content.");
                            }
                            break;
                        }
                    case RenderTreeEditType.PermutationListEntry:
                        throw new NotImplementedException($"Not supported edit type: {edit.Type}");
                    case RenderTreeEditType.PermutationListEnd:
                        throw new NotImplementedException($"Not supported edit type: {edit.Type}");
                    default:
                        throw new NotImplementedException($"Invalid edit type: {edit.Type}");
                }
            }
        }

        private void ApplyRemoveFrame(int siblingIndex)
        {
            var childToRemove = Children[siblingIndex];
            Children.RemoveAt(siblingIndex);
            childToRemove.RemoveSelfAndDescendants();
        }

        private void RemoveSelfAndDescendants()
        {
            if (_targetElement != null)
            {
                // This adapter represents a physical element, so by removing it, we implicitly
                // remove all descendants.
                Renderer.ElementManager.RemoveChildElement(_closestPhysicalParent, _targetElement);
            }
            else
            {
                // This adapter is just a container for other adapters
                foreach (var child in Children)
                {
                    child.RemoveSelfAndDescendants();
                }
            }
        }

        private void ApplySetAttribute(ref RenderTreeFrame attributeFrame)
        {
            if (_targetElement == null)
            {
                throw new InvalidOperationException($"Trying to apply attribute {attributeFrame.AttributeName} to an adapter that isn't for an element");
            }

            _targetElement.ApplyAttribute(
                attributeFrame.AttributeEventHandlerId,
                attributeFrame.AttributeName,
                attributeFrame.AttributeValue,
                attributeFrame.AttributeEventUpdatesAttributeName);
        }

        private void ApplyRemoveAttribute(string removedAttributeName)
        {
            if (_targetElement == null)
            {
                throw new InvalidOperationException($"Trying to remove attribute {removedAttributeName} to an adapter that isn't for an element");
            }

            _targetElement.ApplyAttribute(
                attributeEventHandlerId: 0,
                attributeName: removedAttributeName,
                attributeValue: null,
                attributeEventUpdatesAttributeName: null);
        }

        private int ApplyPrependFrame(RenderBatch batch, int componentId, int siblingIndex, RenderTreeFrame[] frames, int frameIndex, HashSet<int> processedComponentIds)
        {
            ref var frame = ref frames[frameIndex];
            switch (frame.FrameType)
            {
                case RenderTreeFrameType.Element:
                    {
                        InsertElement(siblingIndex, frames, frameIndex, componentId, batch, processedComponentIds);
                        return 1;
                    }
                case RenderTreeFrameType.Component:
                    {
                        // Components are represented by NativeComponentAdapter
                        var childAdapter = Renderer.CreateAdapterForChildComponent(_targetElement ?? _closestPhysicalParent, frame.ComponentId);
                        childAdapter.Name = $"For: '{frame.Component.GetType().FullName}'";
                        childAdapter._targetComponent = frame.Component;

                        AddChildAdapter(siblingIndex, childAdapter);

                        // Apply edits for child component recursively.
                        // That is done to fully initialize elements before adding to the UI tree.
                        processedComponentIds.Add(frame.ComponentId);

                        for (var i = 0; i < batch.UpdatedComponents.Count; i++)
                        {
                            var componentEdits = batch.UpdatedComponents.Array[i];
                            if (componentEdits.ComponentId == frame.ComponentId && componentEdits.Edits.Count > 0)
                            {
                                childAdapter.ApplyEdits(frame.ComponentId, componentEdits.Edits, batch.ReferenceFrames, batch, processedComponentIds);
                            }
                        }

                        return 1;
                    }
                case RenderTreeFrameType.Region:
                    {
                        return InsertFrameRange(batch, componentId, siblingIndex, frames, frameIndex + 1, frameIndex + frame.RegionSubtreeLength, processedComponentIds);
                    }
                case RenderTreeFrameType.Markup:
                    {
                        if (_targetElement is IHandleChildContentText handleChildContentText)
                        {
                            handleChildContentText.HandleText(siblingIndex, frame.MarkupContent);
                        }
                        else if (!string.IsNullOrWhiteSpace(frame.MarkupContent))
                        {
                            throw new NotImplementedException("Nonempty markup: " + frame.MarkupContent);
                        }
#pragma warning disable CA2000 // Dispose objects before losing scope; adapters are disposed when they are removed from the adapter tree
                        var childAdapter = CreateAdapter(_targetElement ?? _closestPhysicalParent);
#pragma warning restore CA2000 // Dispose objects before losing scope
                        childAdapter.Name = $"Markup, sib#={siblingIndex}";
                        AddChildAdapter(siblingIndex, childAdapter);
                        return 1;
                    }
                case RenderTreeFrameType.Text:
                    {
                        if (_targetElement is IHandleChildContentText handleChildContentText)
                        {
                            handleChildContentText.HandleText(siblingIndex, frame.TextContent);
                        }
                        else if (!string.IsNullOrWhiteSpace(frame.TextContent))
                        {
                            throw new NotImplementedException("Nonempty text: " + frame.TextContent);
                        }
#pragma warning disable CA2000 // Dispose objects before losing scope; adapters are disposed when they are removed from the adapter tree
                        var childAdapter = CreateAdapter(_targetElement ?? _closestPhysicalParent);
#pragma warning restore CA2000 // Dispose objects before losing scope
                        childAdapter.Name = $"Text, sib#={siblingIndex}";
                        AddChildAdapter(siblingIndex, childAdapter);
                        return 1;
                    }
                default:
                    throw new NotImplementedException($"Not supported frame type: {frame.FrameType}");
            }
        }

        private NativeComponentAdapter CreateAdapter(IElementHandler physicalParent)
        {
            return new NativeComponentAdapter(Renderer, physicalParent);
        }

        private void InsertElement(int siblingIndex, RenderTreeFrame[] frames, int frameIndex, int componentId, RenderBatch batch, HashSet<int> processedComponentIds)
        {
            // Elements represent native elements
            ref var frame = ref frames[frameIndex];
            var elementName = frame.ElementName;

            IElementHandler elementHandler;
            if (_targetComponent is IElementHandler targetHandler)
            {
                elementHandler = targetHandler;
            }
            else if (ElementHandlerRegistry.ElementHandlers.TryGetValue(elementName, out var elementHandlerFactory))
            {
                elementHandler = elementHandlerFactory(Renderer, _closestPhysicalParent, _targetComponent);
            }
            else
            {
                throw new InvalidOperationException($"Failed to find ElementHandler for '{elementName}'");
            }

            if (_targetComponent is NativeControlComponentBase componentInstance)
            {
                componentInstance.SetElementReference(elementHandler);
            }

            if (siblingIndex != 0)
            {
                // With the current design, we should be able to ignore sibling indices for elements,
                // so bail out if that's not the case
                throw new NotSupportedException($"Currently we assume all adapter elements render exactly zero or one elements. Found an element with sibling index {siblingIndex}");
            }

            _targetElement = elementHandler;

            // For most elements we should add element as child after all properties to have them fully initialized before rendering.
            // However, INonPhysicalChild elements are not real elements, but apply to parent instead, therefore should be added as child before any properties are set.
            if (elementHandler is INonPhysicalChild)
            {
                AddElementAsChildElement();
            }

            var endIndexExcl = frameIndex + frames[frameIndex].ElementSubtreeLength;
            for (var descendantIndex = frameIndex + 1; descendantIndex < endIndexExcl; descendantIndex++)
            {
                var candidateFrame = frames[descendantIndex];
                if (candidateFrame.FrameType == RenderTreeFrameType.Attribute)
                {
                    ApplySetAttribute(ref candidateFrame);
                }
                else
                {
                    // As soon as we see a non-attribute child, all the subsequent child frames are
                    // not attributes, so bail out and insert the remnants recursively
                    InsertFrameRange(batch, componentId, childIndex: 0, frames, descendantIndex, endIndexExcl, processedComponentIds);
                    break;
                }
            }

            if (elementHandler is not INonPhysicalChild)
            {
                AddElementAsChildElement();
            }
        }

        /// <summary>
        /// Add element as a child element for closest physical parent.
        /// </summary>
        private void AddElementAsChildElement()
        {
            var elementIndex = GetIndexForElement();
            Renderer.ElementManager.AddChildElement(_closestPhysicalParent, _targetElement, elementIndex);
        }

        /// <summary>
        /// Finds the sibling index to insert this adapter's element into. It walks up Parent adapters to find 
        /// an earlier sibling that has a native element, and uses that native element's physical index to determine
        /// the location of the new element.
        /// <code>
        /// * Adapter0
        /// * Adapter1
        /// * Adapter2
        /// * Adapter3 (native)
        ///     * Adapter3.0 (searchOrder=2)
        ///         * Adapter3.0.0 (searchOrder=3)
        ///         * Adapter3.0.1 (native)  (searchOrder=4) &lt;-- This is the nearest earlier sibling that has a physical element)
        ///         * Adapter3.0.2
        ///     * Adapter3.1 (searchOrder=1)
        ///         * Adapter3.1.0 (searchOrder=0)
        ///         * Adapter3.1.1 (native) &lt;-- Current adapter
        ///         * Adapter3.1.2
        ///     * Adapter3.2
        /// * Adapter4
        /// </code>
        /// </summary>
        /// <returns>The index at which the native element should be inserted into within the parent. It returns -1 as a failure mode.</returns>
        private int GetIndexForElement()
        {
            var childAdapter = this;
            var parentAdapter = Parent;
            while (parentAdapter != null)
            {
                // Walk previous siblings of this level and deep-scan them for native elements
                var matchedEarlierSibling = GetEarlierSiblingMatch(parentAdapter, childAdapter);
                if (matchedEarlierSibling != null)
                {
                    // If a native element was found somewhere within this sibling, the index for the new element
                    // will be 1 greater than its native index.
                    return Renderer.ElementManager.GetChildElementIndex(_closestPhysicalParent, matchedEarlierSibling._targetElement) + 1;
                }

                // If this level has a native element and all its relevant children have been scanned, then there's
                // no previous sibling, so the new element to be added will be its earliest child (index=0). (There
                // might be *later* siblings, but they are not relevant to this search.)
                if (parentAdapter._targetElement != null)
                {
                    Debug.Assert(parentAdapter._targetElement == _closestPhysicalParent, $"Expected that nearest parent ({parentAdapter.DebugName}) with native element ({parentAdapter._targetElement.GetType().FullName}) would have the closest physical parent ({_closestPhysicalParent.GetType().FullName}).");
                    return 0;
                }

                // If we haven't found a previous sibling with a native element or reached a native container, keep
                // walking up the parent tree...
                childAdapter = parentAdapter;
                parentAdapter = parentAdapter.Parent;
            }
            Debug.Fail($"Expected to find a parent with a native element but found none.");
            return -1;
        }

        private static NativeComponentAdapter GetEarlierSiblingMatch(NativeComponentAdapter parentAdapter, NativeComponentAdapter childAdapter)
        {
            var indexOfParentsChildAdapter = parentAdapter.Children.IndexOf(childAdapter);

            for (var i = indexOfParentsChildAdapter - 1; i >= 0; i--)
            {
                var sibling = parentAdapter.Children[i];
                if (sibling._targetElement is INonChildContainerElement)
                {
                    continue;
                }

                // Deep scan this sibling adapter to find its latest and highest native element
                var siblingWithNativeElement = sibling.GetLastDescendantWithPhysicalElement();
                if (siblingWithNativeElement != null)
                {
                    return siblingWithNativeElement;
                }
            }

            // No preceding sibling has any native elements
            return null;
        }

        private NativeComponentAdapter GetLastDescendantWithPhysicalElement()
        {
            if (_targetElement is INonChildContainerElement)
            {
                return null;
            }
            if (_targetElement != null)
            {
                // If this adapter has a target element, then this is the droid we're looking for. It can't be
                // any children of this target element because they can't be children of this element's parent.
                return this;
            }

            for (var i = Children.Count - 1; i >= 0; i--)
            {
                var child = Children[i];
                var physicalDescendant = child.GetLastDescendantWithPhysicalElement();
                if (physicalDescendant != null)
                {
                    return physicalDescendant;
                }
            }

            return null;
        }

        private int InsertFrameRange(RenderBatch batch, int componentId, int childIndex, RenderTreeFrame[] frames, int startIndex, int endIndexExcl, HashSet<int> processedComponentIds)
        {
            var origChildIndex = childIndex;
            for (var frameIndex = startIndex; frameIndex < endIndexExcl; frameIndex++)
            {
                ref var frame = ref batch.ReferenceFrames.Array[frameIndex];
                var numChildrenInserted = ApplyPrependFrame(batch, componentId, childIndex, frames, frameIndex, processedComponentIds);
                childIndex += numChildrenInserted;

                // Skip over any descendants, since they are already dealt with recursively
                frameIndex += CountDescendantFrames(frame);
            }

            return (childIndex - origChildIndex); // Total number of children inserted     
        }

        private static int CountDescendantFrames(RenderTreeFrame frame)
        {
            return frame.FrameType switch
            {
                // The following frame types have a subtree length. Other frames may use that memory slot
                // to mean something else, so we must not read it. We should consider having nominal subtypes
                // of RenderTreeFramePointer that prevent access to non-applicable fields.
                RenderTreeFrameType.Component => frame.ComponentSubtreeLength - 1,
                RenderTreeFrameType.Element => frame.ElementSubtreeLength - 1,
                RenderTreeFrameType.Region => frame.RegionSubtreeLength - 1,
                _ => 0,
            };
            ;
        }

        private void AddChildAdapter(int siblingIndex, NativeComponentAdapter childAdapter)
        {
            childAdapter.Parent = this;

            if (siblingIndex <= Children.Count)
            {
                Children.Insert(siblingIndex, childAdapter);
            }
            else
            {
                Debug.WriteLine($"WARNING: {nameof(AddChildAdapter)} called with {nameof(siblingIndex)}={siblingIndex}, but Children.Count={Children.Count}");
                Children.Add(childAdapter);
            }
        }

        public void Dispose()
        {
            if (_targetElement is IDisposable disposableTargetElement)
            {
                disposableTargetElement.Dispose();
            }
        }
    }
}
