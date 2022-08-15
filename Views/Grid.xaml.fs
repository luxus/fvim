﻿namespace FVim

open FVim.ui
open FVim.wcwidth
open FVim.def
open FVim.common
open FVim.widgets

open ReactiveUI
open Avalonia
open Avalonia.Controls
open Avalonia.Markup.Xaml
open Avalonia.Threading
open Avalonia.Platform
open Avalonia.Media.Imaging
open Avalonia.VisualTree
open System
open FSharp.Control.Reactive
open Avalonia.Data
open Avalonia.Visuals.Media.Imaging

module private GridHelper =
  let inline trace vm fmt =
    let nr =
      if vm <> Unchecked.defaultof<_> then (vm :> IGridUI).Id.ToString() else "(no vm attached)"
    FVim.log.trace ("editor #" + nr) fmt

open GridHelper
open model
open Avalonia.Input.TextInput
open Avalonia.Input
open Avalonia.Media
open SkiaSharp
open Avalonia.Skia

type Grid() as this =
  inherit Canvas()

  static let ViewModelProperty = AvaloniaProperty.Register<Grid, GridViewModel>("ViewModel")
  static let GridIdProperty = AvaloniaProperty.Register<Grid, int>("GridId")
  static let RenderTickProperty = AvaloniaProperty.Register<Grid, int>("RenderTick")
  static let EnableImeProperty = AvaloniaProperty.Register<Grid, bool>("EnableIme")

  let mutable grid_fb: RenderTargetBitmap = null
  let mutable grid_dc: IDrawingContextImpl = null
  let mutable grid_canvas: SKCanvas = null
  let mutable grid_scale: float = 1.0
  let mutable grid_vm: GridViewModel = Unchecked.defaultof<_>

  let mutable m_cursor: FVim.Cursor = Unchecked.defaultof<_>
  #if DEBUG
  let mutable m_debug = true
  #else
  let mutable m_debug = false
  #endif
  let mutable m_me_focus = false
  let mutable m_scrollbar_fg, m_scrollbar_bg, _, _ = theme.getSemanticHighlightGroup SemanticHighlightGroup.PmenuSbar
  let mutable m_gadget_pen = Pen()
  let mutable m_gadget_brush = SolidColorBrush()
  let m_gridComparer = GridViewModel.MakeGridComparer()

  let ev_cursor_rect_changed = Event<EventHandler,EventArgs>()
  let ev_text_view_visual_changed = Event<EventHandler,EventArgs>()
  let ev_active_state_changed = Event<EventHandler,EventArgs>()
  let ev_surrounding_text_changed = Event<EventHandler, EventArgs>()

  // !Only call this if VisualRoot is attached
  let resizeFrameBuffer() =
  #if DEBUG
    trace grid_vm "resizeFrameBuffer bufw=%A bufh=%A" grid_vm.BufferWidth grid_vm.BufferHeight
  #endif
    grid_scale <- this.GetVisualRoot().RenderScaling
    if grid_fb <> null then 
      grid_fb.Dispose()
      grid_dc.Dispose()
    grid_fb <- AllocateFramebuffer (grid_vm.BufferWidth) (grid_vm.BufferHeight) grid_scale
    grid_dc <- grid_fb.CreateDrawingContext(null)
    grid_canvas <- (grid_dc :?> ISkiaDrawingContextImpl).SkCanvas
    let _ = grid_canvas.Save()
    if this.Bounds.Size.Width - grid_vm.BufferWidth > grid_vm.GlyphWidth * 2.0 
       || this.Bounds.Size.Height - grid_vm.BufferHeight > grid_vm.GlyphHeight * 2.0 then
       this.InvalidateMeasure()

  //-------------------------------------------------------------------------
  //           = The rounding error of the rendering system =
  //
  // Suppose our grid is arranged uniformly with the height of the font:
  //
  //   Y_line = row * H_font
  //
  // Here, row is an integer and H_font float. We then have line Y positions
  // as a sequence of incrementing floats: [ 0 * H_font; 1 * H_font; ... ]
  // Suppose the whole grid is rendered in one pass, the lines will be drawn
  // with coordinates:
  //
  //   [ {0Hf, 1Hf}; {1Hf, 2Hf}; {2Hf, 3Hf} ... ]
  //
  // Clearly this is overlapping. In a pixel-based coordinate system we simply
  // reduce the line height by one pixel. However now we are in a float co-
  // ordinate system.. The overlapped rectangles are drawn differently -- not
  // only that they don't overlap, they leave whitespace gaps in between!
  // To compensate, we have to manually do the rounding to snap the pixels...
  //-------------------------------------------------------------------------
  // like this:
  let rounding(pt: Point) =
    let px = pt * grid_scale
    Point(Math.Ceiling px.X, Math.Ceiling px.Y) / grid_scale

  let mutable _render_glyph_buf: uint[] = [||]
  let mutable _render_char_buf: char[] = [||]

  let drawBuffer (vm: GridViewModel) (ctx: IDrawingContextImpl) (vm_bounds: Rect) row col colend hlid (issym: bool) =

    if col = colend then () else

    let font, fontwide, fontsize = grid_vm.FontAttrs
    let fg, bg, sp, attrs = theme.GetDrawAttrs hlid
    let typeface = GetTypeface(vm.[row, col].text, attrs.italic, attrs.bold, font, fontwide)

    let glyph_type = wswidth vm.[row, colend - 1].text
    let nr_col =
      match glyph_type with
      | CharType.Wide
      | CharType.Nerd
      | CharType.Emoji -> colend - col + 1
      | _ -> colend - col
    let clip = 
      match glyph_type with
      | CharType.Nerd
      | CharType.Emoji -> true
      | _ -> issym

    let abs_r,abs_c = vm.AbsAnchor
    let topLeft = grid_vm.GetPoint (row + abs_r) (col + abs_c)
    let bottomRight = topLeft + grid_vm.GetPoint 1 nr_col
    let bg_region = Rect(topLeft, bottomRight)

    let txt =
      if nr_col > 1 && nr_col < 5 && issym && states.font_ligature then
        if _render_char_buf.Length < (colend - col) * 2 then
          _render_char_buf <- Array.zeroCreate ((colend - col) * 2)
        let mutable _len = 0
        for i = col to colend - 1 do
          Rune.feed(vm.[row, i].text, _render_char_buf, &_len)
        Shaped <| ReadOnlyMemory(_render_char_buf, 0, _len)
      else
        if _render_glyph_buf.Length < (colend - col) * 2 then
          _render_glyph_buf <- Array.zeroCreate ((colend - col)*2)
        let mutable _len = 0
        for i = col to colend - 1 do
          Rune.feed(vm.[row, i].text, _render_glyph_buf, &_len)
        Unshaped <| ReadOnlyMemory(_render_glyph_buf, 0, _len)

    try
        RenderText(ctx, bg_region, vm_bounds, fg, bg, sp, attrs.underline, attrs.undercurl, txt, typeface, fontsize, clip)
    with ex -> trace vm "drawBuffer: %s" (ex.ToString())

  // assembles text from grid and draw onto the context.
  let drawBufferLine (vm: GridViewModel) (ctx: IDrawingContextImpl) (vm_bounds: Rect) y x0 xN =
    let xN = min xN vm.Cols
    let x0 = max x0 0
    let y = Math.Clamp(y, 0, (vm.Rows - 1))
    let mutable x': int = xN - 1
    let mutable wc: CharType = wswidth vm.[y, x'].text
    let mutable sym: bool = isProgrammingSymbol vm.[y, x'].text
    let mutable prev_hlid = vm.[y, x'].hlid
    let mutable nr_symbols = if sym then 1 else 0

    let mutable bold =
      let _, _, _, hl_attrs = theme.GetDrawAttrs prev_hlid
      hl_attrs.bold
    //  in each line we do backward rendering.
    //  the benefit is that the italic fonts won't be covered by later drawings
    for x = xN - 2 downto x0 do
      let current = vm.[y, x]
      let mytext = current.text
      //  !NOTE text shaping is slow. We only use shaping for
      //  a symbol-only span (for ligature drawing) with width > 1,
      //  That is, a single symbol will be merged into adjacent spans.
      let mysym = 
        let v = isProgrammingSymbol mytext
        if not v then
          nr_symbols <- 0
          false
        else
          nr_symbols <- nr_symbols + 1
          nr_symbols > 1
      let mywc = wswidth mytext
      //  !NOTE bold glyphs are generally wider than normal.
      //  Therefore, we have to break them into single glyphs
      //  to prevent overflow into later cells.
      let hlidchange = prev_hlid <> current.hlid
      if hlidchange || mywc <> wc || bold || sym <> mysym || mywc = CharType.Braille then
        //  If the span split is caused by symbols, we put [x+1]
        //  into the current span because nr_symbols has latched one rune.
        let prev_span = if mysym && not sym && mywc = wc && not bold && not hlidchange 
                        then x + 2 
                        else x + 1
        drawBuffer vm ctx vm_bounds y prev_span (x' + 1) prev_hlid sym
        x' <- prev_span - 1
        wc <- mywc
        sym <- mysym
        if hlidchange then
          prev_hlid <- current.hlid
          bold <-
            let _, _, _, hl_attrs = theme.GetDrawAttrs prev_hlid
            hl_attrs.bold
    drawBuffer vm ctx vm_bounds y x0 (x' + 1) prev_hlid sym

  let doWithDataContext fn =
    match this.DataContext with
    | :? GridViewModel as viewModel -> fn viewModel
    | _ -> Unchecked.defaultof<_>

  //let findChildEditor(vm: obj) = this.Children |> Seq.tryFind(fun x -> x.DataContext = vm)

  let onViewModelConnected(vm: GridViewModel) =
    grid_vm <- vm
    trace grid_vm "viewmodel connected"
    resizeFrameBuffer()
    vm.Watch
      [ Observable.merge (vm.ObservableForProperty(fun x -> x.BufferWidth))
          (vm.ObservableForProperty(fun x -> x.BufferHeight))
        |> Observable.subscribe(fun _ ->
             if this.GetVisualRoot() <> null then resizeFrameBuffer())

        Observable.interval(TimeSpan.FromMilliseconds 100.0)
        |> Observable.firstIf(fun _ -> this.IsInitialized && vm.Height > 0.0 && vm.Width > 0.0)
        |> Observable.subscribe(fun _ ->
             model.OnGridReady(vm :> IGridUI)
             if vm.Focusable then
               ignore <| Dispatcher.UIThread.InvokeAsync(this.Focus))

        this.GetObservable(RenderTickProperty).Subscribe(fun id -> 
          trace grid_vm "render tick %d" id
          this.InvalidateVisual())

        this.GetObservable(EnableImeProperty).Subscribe(fun _ -> 
            ev_active_state_changed.Trigger(this, EventArgs.Empty)
        )

        this.GotFocus.Subscribe(fun _ -> 
            m_me_focus <- true
            vm.IsFocused <- true
            m_me_focus <- false
        )
        this.LostFocus.Subscribe(fun _ -> 
            m_me_focus <- true
            vm.IsFocused <- false
            m_me_focus <- false
        )

        vm.ObservableForProperty(fun x -> x.IsFocused)
        |> Observable.subscribe(fun focused ->
          if focused.Value && not this.IsFocused && not m_me_focus then
            trace grid_vm "viewmodel ask to focus"
            let win = this.GetVisualRoot() :?> Window
            win.Activate()
            this.Focus()
          )
      ]

  let subscribeAndHandleInput fn (ob: IObservable<#Avalonia.Interactivity.RoutedEventArgs>) =
    ob.Subscribe(fun e ->
      if not e.Handled then
        doWithDataContext(fn e))

  // prevent repetitive drawings
  let _drawnRegions = ResizeArray()
  let _drawVMs = ResizeArray()

  let rec scanDrawVMs (vm: GridViewModel) =
    if vm.Hidden then ()
    else
    _drawVMs.Add(vm)
    vm.ChildGrids |> Seq.iter scanDrawVMs

  let drawOps (vm: GridViewModel) gw gh = 
    let abs_r,abs_c = vm.AbsAnchor
    // clip each vm individually to prevent shaped text run overflow into the root grid...
    let vm_bounds = Rect(float abs_c * gw, float abs_r * gh, float vm.Cols * gw, float vm.Rows * gh)
    // if other grids tainted the region, mark it dirty
    let touched = _drawnRegions 
                  |> Seq.exists(fun(r,c,ce) -> 
                  abs_r <= r &&
                  r < abs_r + vm.Rows &&
                  not( abs_c >= ce || c >= abs_c + vm.Cols ))
    if touched then vm.MarkDirty()

    if vm.Hidden then false
    elif vm.Dirty then
    #if DEBUG
        trace vm "drawing whole grid"
    #endif
        for row = 0 to vm.Rows - 1 do
            drawBufferLine vm grid_dc vm_bounds row 0 vm.Cols
            _drawnRegions.Add(row + abs_r, abs_c, vm.Cols + abs_c)
        true
    else
    // not tainted. can draw with my draw ops.
    let draw row col colend = 
      let covered = _drawnRegions |> Seq.exists(fun (r, c, ce) -> r = row + abs_r && c <= col + abs_c && ce >= colend + abs_c)
      if not covered then
        drawBufferLine vm grid_dc vm_bounds row col colend
        _drawnRegions.Add(row + abs_r, col + abs_c, colend + abs_c)
      else
        ()

    vm.DrawOps |> Seq.iter (
      function 
      | Scroll (top, bot, left, right, row, _col) ->
        let (t, l, b, r) = 
          if row > 0 then (top, left, bot - row, right)
          else (top - row, left, bot, right)
        for row = t to b - 1 do
          draw row l r
      | Put r -> 
        for row = r.row to r.row_end - 1 do
          draw row r.col r.col_end
    )
    if vm.DrawMsgSeparator then
        let pen = 
            theme.semhl.TryFind SemanticHighlightGroup.MsgSeparator
            >>= (fun i -> 
                let fg, _, _, _ = theme.GetDrawAttrs i
                Some fg)
            |> Option.defaultValue Colors.Gray
            |> (fun c -> Pen(c.ToUint32(), thickness = 1.0))
        let y = float abs_r * grid_vm.GlyphHeight + 0.5
        grid_dc.DrawLine(pen, Point(0.0,y), Point(grid_fb.Size.Width, y))

    vm.DrawOps.Count <> 0

  /// draw add-ons attached to the grids. for example:
  /// - a scrollbar
  /// - graphical overlays
  /// - ...
  /// no need to worry about the grid content being overwritten here
  /// because we are working with the drawing context directly, not 
  /// the grid framebuffer.
  let drawGadgets (vm: GridViewModel) (ctx: DrawingContext) gw gh =
    let vm_x, vm_y, vm_w, vm_h = 
      let y1,x1 = vm.AbsAnchor
      let w,h = vm.Cols,vm.Rows
      float x1 * gw, float y1 * gh, float w * gw, float h * gh
    let view_top,view_bot,cur_row,line_count = 
      let top,bot,row,_,lc = vm.ScrollbarData
      float top, float bot, float row, float lc
    use _mainTr = ctx.PushPreTransform(Matrix.CreateTranslation(vm_x, vm_y))
    use _mainClip = ctx.PushClip(Rect(0, 0, vm_w, vm_h))
    // infer wincol from viewport + cursor info
    let wincol = vm.WinCol

    // gui widgets
    let placements = getGuiWidgetPlacements vm.BufNr
    let drawGuiWidgets ({ns = ns; mark = mark; col = mc}, {trow=r;tcol=c}) =
      if ns = guiwidgetNamespace then
        match (placements.TryGetValue mark) with
        | false, _ -> ()
        | true, ({widget=wid; w=grid_w; h=grid_h} as p) ->
          let computed_col = mc - wincol
          let c = min c computed_col
          let hideAttr = p.GetHideAttr()
          let singleLine = grid_h = 1
          let lineOverlap = vm.CursorInfo.row |> (r <<-> r+grid_h)
          let colOverlap = vm.CursorInfo.col |> (c <<-> c+grid_w)
          let hide = match hideAttr with
                     | CursorOverlap -> lineOverlap && colOverlap
                     | CursorLineOverlap -> lineOverlap
                     | _ -> false
          if hide then () else
          let r, c, w, h = float r * gh, float c * gw, float grid_w * gw, float grid_h * gh
          let bounds = Rect(c, r, w, h)
          use _clip = ctx.PushClip(bounds)
          let widget = getGuiWidget wid
          match widget with
          | BitmapWidget img ->
            let src, dst = p.GetDrawingBounds img.Size bounds
            ctx.DrawImage(img, src, dst)
          | VectorImageWidget (img) ->
            let src, dst = p.GetDrawingBounds (Size(img.Width,img.Height)) bounds
            let themed, svg_fg, svg_bg = 
              p.GetSvgAttr(hideAttr, lineOverlap, colOverlap, singleLine)
            if themed then
              m_gadget_brush.Color <- svg_bg
              ctx.FillRectangle(m_gadget_brush, bounds)
            let scaleMatrix = Matrix.CreateScale(dst.Width / src.Width, dst.Height / src.Height)
            let translateMatrix = Matrix.CreateTranslation(dst.Left, dst.Top)
            use _tr = ctx.PushPreTransform(scaleMatrix * translateMatrix)
            if themed then
              img.SetTheme(m_gadget_brush, svg_fg, svg_bg)
            img.Draw(ctx)
          | PlainTextWidget(text) ->
            let fg, bg, font, size = p.GetTextAttr()
            m_gadget_brush.Color <- bg
            ctx.FillRectangle(m_gadget_brush, bounds)
            m_gadget_brush.Color <- fg
            let text = FormattedText(text, font, size, TextAlignment.Left, TextWrapping.Wrap, bounds.Size)
            ctx.DrawText(m_gadget_brush, bounds.TopLeft, text)
          | _ -> ()
    for ({mark=mark} as m, cell, pos) in vm.Extmarks.Values do
      // if cell.marks does not have this mark, it means cell has scrolled out of view.
      if cell.ContainsMark mark then
        drawGuiWidgets(m,pos)
    for tup in vm.ExtmarksOob.Values do
      drawGuiWidgets tup

    let scrollbar_bg_color = m_scrollbar_bg.ToUint32() ||| 0xff000000u
                             |> Color.FromUInt32
    let scrollbar_cursor_color = grid_vm.GetRootGrid().CursorInfo.bg

    let drawScrollbar() = 
      // scrollbar
      // todo mouse over opacity adjustment
      let bar_w = 8.0
      let slide_w = 7.0
      let sign_w = 4.0
      // -- bg
      let bar_x = vm_w - bar_w
      m_gadget_brush.Color <- scrollbar_bg_color
      m_gadget_brush.Opacity <- 0.5 
      ctx.FillRectangle(m_gadget_brush, Rect(bar_x, 0, bar_w, vm_h))
      // -- fg
      if view_bot > view_top && line_count > 0.0 then
        let bot = min view_bot line_count
        let slide_x = vm_w - (bar_w + slide_w) / 2.0
        let slide_p1, slide_p2 = view_top / line_count, bot / line_count
        let slide_h = (slide_p2 - slide_p1) * vm_h
        let slide_y = slide_p1 * vm_h
        m_gadget_brush.Color <- m_scrollbar_fg
        m_gadget_brush.Opacity <- 0.5
        ctx.FillRectangle(m_gadget_brush, Rect(slide_x, slide_y, slide_w, slide_h))
      // -- cursor
      if line_count > 0.0 then
        let cur_y = cur_row / line_count * vm_h
        let cur_h = 2.0
        m_gadget_brush.Color <- scrollbar_cursor_color
        m_gadget_brush.Opacity <- 1.0
        ctx.FillRectangle(m_gadget_brush, Rect(bar_x, cur_y, bar_w, cur_h))
      // -- signs
      if line_count > 0.0 then
        let sign_h = max (vm_h / line_count) 4.0
        let xl = vm_w - bar_w
        let xr = vm_w - sign_w
        let signs = getSignPlacements vm.BufNr
        for {line=line;kind=kind} in signs do
          let color,sx = match kind with
                         | SignKind.Warning -> Colors.Green,xr
                         | SignKind.Error -> Colors.Red,xr
                         | SignKind.Add -> Colors.LightGreen,xl
                         | SignKind.Delete -> Colors.LightCoral,xl
                         | SignKind.Change -> Colors.Yellow,xl
                         | _ -> Colors.Transparent,xl
          let sy = float line / line_count * vm_h
          m_gadget_brush.Color <- color
          m_gadget_brush.Opacity <- 1.0
          ctx.FillRectangle(m_gadget_brush, Rect(sx, sy, sign_w, sign_h))
    // if not (vm.IsMsg || vm.IsFloat) then
    //  drawScrollbar()

    let drawDebug() =
      let txt = Media.FormattedText()
      txt.Text <- $"Grid #{vm.GridId}, Z={vm.ZIndex}, S={vm.CreateSeq}"
      txt.Typeface <- Media.Typeface(ui.DefaultFont)
      txt.FontSize <- 20

      ctx.DrawText(Media.Brushes.Red, Point(0,0), txt)

      ctx.DrawRectangle(Media.Pen(Media.Brushes.Red, 3.0), Rect(0, 0, vm_w, vm_h))
      ctx.DrawLine(Media.Pen(Media.Brushes.Red, 1.0), Point(0.0, 0.0), Point(vm_w, vm_h))
      ctx.DrawLine(Media.Pen(Media.Brushes.Red, 1.0), Point(0.0, vm_h), Point(vm_w, 0.0))

    if m_debug then drawDebug()

  do
    this.Watch
      [ this.GetObservable(Grid.DataContextProperty)
        |> Observable.ofType
        |> Observable.zip this.AttachedToVisualTree
        |> Observable.map snd
        |> Observable.subscribe onViewModelConnected

        this.Bind(Canvas.LeftProperty, Binding("X"))
        this.Bind(Canvas.TopProperty, Binding("Y"))

        rpc.register.watch "font" (fun () ->
          if grid_vm <> Unchecked.defaultof<_> then
            grid_vm.MarkDirty()
            this.InvalidateVisual())

        rpc.register.notify "EnableIme" (fun [| Bool(v) |] -> 
            this.EnableIme <- v)

        //  Input handling
        this.TextInput |> subscribeAndHandleInput(fun e vm -> vm.OnTextInput e)
        this.KeyDown |> subscribeAndHandleInput(fun e vm -> vm.OnKey e)
        this.PointerPressed |> subscribeAndHandleInput(fun e vm -> vm.OnMouseDown e this)
        this.PointerReleased |> subscribeAndHandleInput(fun e vm -> vm.OnMouseUp e this)
        this.PointerMoved |> subscribeAndHandleInput(fun e vm -> vm.OnMouseMove e this)
        this.PointerWheelChanged |> subscribeAndHandleInput(fun e vm -> vm.OnMouseWheel e this)

        //  Theming
        theme.themeconfig_ev.Publish 
        |> Observable.subscribe (fun (_,_,_,_,f,b,_,_) -> 
            m_scrollbar_bg <- b
            m_scrollbar_fg <- f)
      ]
    AvaloniaXamlLoader.Load(this)
  static do
    InputElement.TextInputMethodClientRequestedEvent.AddClassHandler<Grid>(
        fun grid e -> e.Client <- grid
    ) |> ignore

  override _.Render ctx =
    if isNull grid_fb then
      trace grid_vm "grid_fb is null"
    else
#if DEBUG
    let timer = System.Diagnostics.Stopwatch.StartNew()
    use _opacity = ctx.PushOpacity(0.7)
#endif
    _drawnRegions.Clear()
    _drawVMs.Clear()
    scanDrawVMs grid_vm
    _drawVMs.Sort(m_gridComparer)

    // let's assume grid_fb is aligned with root vm row x col.
    // children vm.GlyphHeight/vm.GlyphWidth don't work well here
    // calculate the proper values now:
    let gw,gh = grid_fb.Size.Width/float grid_vm.Cols,grid_fb.Size.Height/float grid_vm.Rows

    let mutable drawn = false
    for vm in _drawVMs do
        let drawn' = drawOps vm gw gh
        drawn <- drawn || drawn'

    grid_vm.MarkClean()

    let src_rect = Rect(0.0, 0.0, float grid_fb.PixelSize.Width, float grid_fb.PixelSize.Height)
    let tgt_rect = Rect(0.0, 0.0, grid_fb.Size.Width, grid_fb.Size.Height)

    ctx.DrawImage(grid_fb, src_rect, tgt_rect, BitmapInterpolationMode.LowQuality)
    for vm in _drawVMs do
        // do not draw gadgets for the root grid / message / floating windows (borders only)
        if vm.GridId <> 1 && not vm.AboveGadgets then 
            drawGadgets vm ctx gw gh

    // now redraw those above gadgets
    let fill_bg, bg = 
      let _, bg, _, _ = theme.GetDrawAttrs 1
      bg.A = 0uy, removeAlpha bg
    let pgw, pgh = src_rect.Width / float grid_vm.Cols, src_rect.Height / float grid_vm.Rows
    for vm in _drawVMs do
      if vm.AboveGadgets then
        let abs_r,abs_c = vm.AbsAnchor
        let r,c,w,h = float abs_r, float abs_c, float vm.Cols, float vm.Rows
        let src_rect' = Rect(c * pgw, r * pgh, w * pgw, h * pgh)
        let dst_rect' = Rect(c * gw, r * gh, w * gw, h * gh)
        if fill_bg then
          m_gadget_brush.Color <- bg
          ctx.FillRectangle(m_gadget_brush, dst_rect')
        ctx.DrawImage(grid_fb, src_rect', dst_rect', BitmapInterpolationMode.LowQuality)
        drawGadgets vm ctx gw gh

#if DEBUG
    timer.Stop()
    if drawn then trace grid_vm "drawing end, time = %dms." timer.ElapsedMilliseconds
    else trace grid_vm "drawing end, nothing drawn."
#endif

  override this.MeasureOverride(size) =
    trace grid_vm "MeasureOverride: %A" size
    doWithDataContext(fun vm ->
      vm.RenderScale <- (this :> IVisual).GetVisualRoot().RenderScaling
      let sz =
        // multigrid: size is top-down managed, which means that
        // the measurement of the view should be consistent with
        // the buffer size calculated from the viewmodel.
        if vm.IsTopLevel && states.ui_multigrid then
          size
        else
          Size(vm.BufferWidth, vm.BufferHeight)
      vm.SetMeasuredSize sz
      sz)

  override this.OnInitialized() =
    m_cursor <- this.FindControl<FVim.Cursor>("cursor")
    this.Watch [
        m_cursor.ObservableForProperty(fun c -> c.Bounds)
        |> Observable.subscribe(fun _ -> 
            ev_cursor_rect_changed.Trigger(this, EventArgs.Empty))
    ]

  interface IViewFor<GridViewModel> with

    member this.ViewModel
      with get (): GridViewModel = this.GetValue(ViewModelProperty)
      and set (v: GridViewModel): unit = this.SetValue(ViewModelProperty, v) |> ignore

    member this.ViewModel
      with get (): obj = this.GetValue(ViewModelProperty) :> obj
      and set (v: obj): unit = this.SetValue(ViewModelProperty, v) |> ignore

  interface ITextInputMethodClient with
      member _.SupportsPreedit = false
      member _.SupportsSurroundingText = false
      member _.SetPreeditText(_) = raise (NotSupportedException())
      member _.SurroundingText = raise (NotSupportedException())
      member _.TextAfterCursor: string = null
      member _.TextBeforeCursor: string = null
      [<CLIEvent>] member _.SurroundingTextChanged: IEvent<EventHandler,EventArgs> = ev_surrounding_text_changed.Publish

      member _.CursorRectangle: Rect = m_cursor.Bounds
      member _.TextViewVisual: IVisual = this :> IVisual
      [<CLIEvent>] member _.CursorRectangleChanged: IEvent<EventHandler,EventArgs> = ev_cursor_rect_changed.Publish
      [<CLIEvent>] member _.TextViewVisualChanged: IEvent<EventHandler,EventArgs> = ev_text_view_visual_changed.Publish
#if HAS_IME_SUPPORT
      member _.ActiveState = this.EnableIme
      [<CLIEvent>] member _.ActiveStateChanged: IEvent<EventHandler,EventArgs> = ev_active_state_changed.Publish
#endif

  member this.GridId
    with get () = this.GetValue(GridIdProperty)
    and set (v: int) = this.SetValue(GridIdProperty, v) |> ignore

  member this.RenderTick
    with get() = this.GetValue(RenderTickProperty)
    and  set(v) = this.SetValue(RenderTickProperty, v) |> ignore

  member this.EnableIme
    with get() = this.GetValue(EnableImeProperty)
    and set(v) = 
        this.SetValue(EnableImeProperty, v) |> ignore

  static member GetGridIdProp() = GridIdProperty
