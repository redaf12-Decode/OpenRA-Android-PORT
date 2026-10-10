#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using Android.Views;
using OpenRA;
using OpenRA.Primitives;

namespace OpenRA.Platforms.Android
{
	// Translates Android MotionEvents (multi-touch) into OpenRA MouseInputs.
	//
	// A finger is not a mouse: there is no second button to split "select" from "order", so the
	// gesture is resolved *before* any button is reported to the engine, which is then driven with
	// the Classic mouse layout (see Settings.MouseControlStyle):
	//
	//   - Tap                    -> left press + release   Select what is under the finger, or give
	//                                                     the order that point implies (move, attack,
	//                                                     enter, repair, ...)
	//   - Double-tap             -> left press + release   Select every unit of that type on screen
	//                                 (MultiTapCount = 2)
	//   - Press and hold, drag   -> left drag              Selection box; also drags UI widgets
	//                                                     (sliders, scroll bars, minimap)
	//   - Drag                   -> right drag             Pan the camera
	//   - Two-finger drag        -> right drag             Pan the camera
	//   - Two-finger pinch       -> MouseInputEvent.Scroll Zoom
	//
	// Nothing at all is reported on the initial press. Reporting a left press straight away would
	// hand mouse focus to WorldInteractionControllerWidget, which then swallows the right-button
	// events the camera pan needs (Ui.HandleInput routes events to the focused widget first).
	sealed class AndroidInput
	{
		enum Gesture
		{
			Idle,       // no finger down
			Pending,    // finger down, gesture not decided yet
			Pan,        // right button held: dragging the camera
			Select,     // left button held after a press-and-hold: selection box / UI drag
			TwoFinger   // a second finger is down: it owns the camera pan
		}

		readonly ConcurrentQueue<PendingInput> pending = new();

		// Primary finger state.
		int primaryPointerId = -1;
		int2 primaryDownPos;
		int2 primaryLastPos;
		Stopwatch primaryDownTimer;

		// Secondary finger state (two-finger pan / pinch).
		int secondaryPointerId = -1;

		// Pinch state.
		float lastPinchDist;

		// Press duration before the left button goes down (press and hold -> selection box).
		const int HoldMs = 400;

		// Movement that turns a press into a camera pan instead.
		const int TouchSlopPx = 16;

		Gesture gesture = Gesture.Idle;

		struct PendingInput
		{
			public MotionEventActions Action;
			public float X;
			public float Y;
			public int PointerId;
			public long TimestampMs;
		}

		public void Enqueue(MotionEvent e, Size windowSize)
		{
			var action = e.ActionMasked;
			var index = e.ActionIndex;

			if (action == MotionEventActions.Move)
			{
				// Forward moves for all tracked fingers.
				for (var i = 0; i < e.PointerCount; i++)
				{
					var pid = e.GetPointerId(i);
					if (pid == primaryPointerId || pid == secondaryPointerId)
					{
						pending.Enqueue(new PendingInput
						{
							Action = action,
							X = e.GetX(i),
							Y = e.GetY(i),
							PointerId = pid,
							TimestampMs = e.EventTime
						});
					}
				}

				// Detect pinch zoom when two fingers are down.
				if (primaryPointerId >= 0 && secondaryPointerId >= 0 && e.PointerCount >= 2)
				{
					var i0 = e.FindPointerIndex(primaryPointerId);
					var i1 = e.FindPointerIndex(secondaryPointerId);
					if (i0 >= 0 && i1 >= 0)
					{
						var dx = e.GetX(i0) - e.GetX(i1);
						var dy = e.GetY(i0) - e.GetY(i1);
						var dist = (float)Math.Sqrt(dx * dx + dy * dy);
						if (lastPinchDist > 0)
						{
							var delta = (int)(dist - lastPinchDist);
							if (Math.Abs(delta) > 2)
							{
								pending.Enqueue(new PendingInput
								{
									Action = MotionEventActions.Scroll,
									X = (e.GetX(i0) + e.GetX(i1)) / 2,
									Y = (e.GetY(i0) + e.GetY(i1)) / 2,
									PointerId = -1,
									TimestampMs = e.EventTime
								});
								// Store the delta in the Y field via a side channel — we'll read it in PumpInput.
								pinchDelta = delta;
							}
						}

						lastPinchDist = dist;
					}
				}
			}
			else
			{
				pending.Enqueue(new PendingInput
				{
					Action = action,
					X = e.GetX(index),
					Y = e.GetY(index),
					PointerId = e.GetPointerId(index),
					TimestampMs = e.EventTime
				});
			}
		}

		int pinchDelta;

		void BeginPan(IInputHandler h, int2 at)
		{
			h.OnMouseInput(new MouseInput(MouseInputEvent.Down, MouseButton.Right, at, int2.Zero, Modifiers.None, 1));

			// Sync Viewport.LastMousePos so Standard (grab and drag) scrolling starts from the press
			// point instead of wherever the pointer happened to be during the previous gesture.
			h.OnMouseInput(new MouseInput(MouseInputEvent.Move, MouseButton.None, at, int2.Zero, Modifiers.None, 0));
		}

		void EndPan(IInputHandler h, int2 at)
		{
			h.OnMouseInput(new MouseInput(MouseInputEvent.Up, MouseButton.Right, at, int2.Zero, Modifiers.None, 1));
		}

		void EmitTap(IInputHandler h, int2 at)
		{
			// DetectFromMouse advances the tap history, so it must be called exactly once per tap.
			var tapCount = MultiTapDetection.DetectFromMouse(0, at);
			h.OnMouseInput(new MouseInput(MouseInputEvent.Down, MouseButton.Left, at, int2.Zero, Modifiers.None, tapCount));
			h.OnMouseInput(new MouseInput(MouseInputEvent.Up, MouseButton.Left, at, int2.Zero, Modifiers.None, tapCount));
		}

		public void PumpInput(IInputHandler inputHandler, Size windowSize, Size surfaceSize, float scale)
		{
			while (pending.TryDequeue(out var p))
			{
				var pos = new int2((int)p.X, (int)p.Y);

				switch (p.Action)
				{
					case MotionEventActions.Down:
						// First finger down. Hold off on reporting anything until the gesture is known:
						// a left press would give mouse focus to the world interaction controller and
						// block the right-button events the camera pan needs.
						primaryPointerId = p.PointerId;
						primaryDownPos = pos;
						primaryLastPos = pos;
						primaryDownTimer = Stopwatch.StartNew();
						gesture = Gesture.Pending;
						break;

					case MotionEventActions.PointerDown:
						if (secondaryPointerId >= 0)
							break;

						secondaryPointerId = p.PointerId;
						lastPinchDist = 0;

						// A second finger always means "pan". Close whatever the first finger was
						// doing first so no button is left dangling, then make sure the right
						// button is down (it is only already down when the first finger was panning).
						if (gesture == Gesture.Select)
							inputHandler.OnMouseInput(new MouseInput(MouseInputEvent.Up, MouseButton.Left, primaryLastPos, int2.Zero, Modifiers.None, 1));

						if (gesture != Gesture.Pan)
							BeginPan(inputHandler, gesture == Gesture.Idle ? pos : primaryDownPos);

						gesture = Gesture.TwoFinger;
						break;

					case MotionEventActions.Move:
						if (p.PointerId == primaryPointerId)
						{
							primaryLastPos = pos;

							switch (gesture)
							{
								case Gesture.Pending:
									// The finger moved before the hold timeout: this is a camera pan,
									// not a tap and not a selection box.
									if ((pos - primaryDownPos).Length > TouchSlopPx)
									{
										gesture = Gesture.Pan;
										BeginPan(inputHandler, primaryDownPos);
										inputHandler.OnMouseInput(new MouseInput(MouseInputEvent.Move, MouseButton.Right, pos, int2.Zero, Modifiers.None, 0));
									}
									break;

								case Gesture.Pan:
									inputHandler.OnMouseInput(new MouseInput(MouseInputEvent.Move, MouseButton.Right, pos, int2.Zero, Modifiers.None, 0));
									break;

								case Gesture.Select:
									inputHandler.OnMouseInput(new MouseInput(MouseInputEvent.Move, MouseButton.Left, pos, int2.Zero, Modifiers.None, 0));
									break;
							}
						}
						else if (p.PointerId == secondaryPointerId && gesture == Gesture.TwoFinger)
						{
							inputHandler.OnMouseInput(new MouseInput(MouseInputEvent.Move, MouseButton.Right, pos, int2.Zero, Modifiers.None, 0));
						}

						break;

					case MotionEventActions.PointerUp:
						if (p.PointerId == secondaryPointerId)
						{
							secondaryPointerId = -1;
							lastPinchDist = 0;

							// Another finger may still be down: it keeps panning on its own, otherwise
							// this was the last finger and the pan has to be released here.
							if (gesture == Gesture.TwoFinger)
							{
								if (primaryPointerId >= 0)
									gesture = Gesture.Pan;
								else
								{
									EndPan(inputHandler, pos);
									gesture = Gesture.Idle;
								}
							}
						}
						else if (p.PointerId == primaryPointerId)
						{
							// The first finger lifted while the second is still down: the second
							// finger keeps the pan alive (it becomes Android's primary pointer).
							primaryPointerId = -1;
							primaryDownTimer = null;
						}

						break;

					case MotionEventActions.Up:
						// The last finger lifted.
						switch (gesture)
						{
							case Gesture.Pending:
								EmitTap(inputHandler, pos);
								break;

							case Gesture.Select:
								// Pressed and held without dragging far enough for a box: a click.
								inputHandler.OnMouseInput(new MouseInput(MouseInputEvent.Up, MouseButton.Left, pos, int2.Zero, Modifiers.None, 1));
								break;

							case Gesture.Pan:
							case Gesture.TwoFinger:
								EndPan(inputHandler, pos);
								break;
						}

						primaryPointerId = -1;
						secondaryPointerId = -1;
						primaryDownTimer = null;
						lastPinchDist = 0;
						gesture = Gesture.Idle;
						break;

					case MotionEventActions.Scroll:
						// Pinch-to-zoom: synthesize a scroll event. The zoom modifier (Ctrl) is needed
						// by ViewportControllerWidget, so we set it to make zoom work without a keyboard.
						inputHandler.OnMouseInput(new MouseInput(MouseInputEvent.Scroll, MouseButton.None, pos, new int2(0, pinchDelta), Modifiers.Ctrl, 0));
						pinchDelta = 0;
						break;
				}
			}

			// Android only reports Move events when the finger actually moves, so a stationary
			// press-and-hold has to be detected from the frame pump rather than from the queue.
			// This is the "press and hold, then drag" gesture: the left button goes down here and
			// any later movement becomes a selection box (or a drag inside a UI widget).
			if (gesture == Gesture.Pending && primaryDownTimer != null && secondaryPointerId < 0 &&
				primaryDownTimer.ElapsedMilliseconds >= HoldMs &&
				(primaryLastPos - primaryDownPos).Length < TouchSlopPx)
			{
				gesture = Gesture.Select;
				inputHandler.OnMouseInput(new MouseInput(MouseInputEvent.Down, MouseButton.Left, primaryLastPos, int2.Zero, Modifiers.None, 1));
			}
		}
	}
}
