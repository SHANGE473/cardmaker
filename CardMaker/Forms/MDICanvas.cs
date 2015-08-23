////////////////////////////////////////////////////////////////////////////////
// The MIT License (MIT)
//
// Copyright (c) 2015 Tim Stair
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
////////////////////////////////////////////////////////////////////////////////

using CardMaker.Card;
using CardMaker.XML;
using Support.UI;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace CardMaker.Forms
{
    public partial class MDICanvas : Form
    {
        private static MDICanvas s_zInstance;

        private const int SELECTION_BUFFER = 3;
        private const int SELECTION_BUFFER_SPACE = SELECTION_BUFFER*2;
        private bool m_bElementSelected;
        private MouseMode m_eMouseMode = MouseMode.MoveResize;
        private ResizeDirection m_eResizeDirection = ResizeDirection.Up;
        private ProjectLayoutElement m_zSelectedElement;
        private List<ProjectLayoutElement> m_listSelectedElements;
        private List<Point> m_listSelectedOffsets;
        private Dictionary<ProjectLayoutElement, Rectangle> m_dictionarySelectedUndo;
        private Point m_pointOriginalMouseDown = Point.Empty;
        private TranslationLock m_eTranslationLock = TranslationLock.Unset;
        private readonly ContextMenuStrip m_zContextMenu = new ContextMenuStrip();

        private CardCanvas m_zCardCanvas;
        private float m_fZoom = 1.0f;
        private float m_fZoomRatio = 1.0f;

        private const int TRANSLATION_LOCK_DEAD_ZONE = 3; // 3 pixels of movement

        private enum MouseMode
        {
            MoveResize,
            Move
        }

        private enum TranslationLock
        {
            Unset,
            WaitingToSet,
            Vertical, // lock to vertical only
            Horizontal, // lock to horizontal only
        }

        [Flags]
        public enum ResizeDirection
        {
            None = 0,
            Up = 1 << 0,
            Down = 1 << 1,
            Left = 1 << 2,
            Right = 1 << 3,
            UpLeft = Up | Left,
            UpRight = Up | Right,
            DownLeft = Down | Left,
            DownRight = Down | Right,
            Move = 1 << 8,
        }

        /// <summary>
        /// The Canvas has to micro-manage the MDIElementControl to allow for control over undo/redo functionality
        /// </summary>
        public bool CanvasUserAction { get; set; }

        private MDICanvas()
        {
            InitializeComponent();
            var zBitmap = new Bitmap(32, 32);
            Graphics zGraphics = Graphics.FromImage(zBitmap);
            Brush zBlack = new SolidBrush(Color.LightGray);
            Brush zWhite = new SolidBrush(Color.White);
            zGraphics.FillRectangle(zBlack, 0, 0, 16, 16);
            zGraphics.FillRectangle(zWhite, 16, 0, 16, 16);
            zGraphics.FillRectangle(zBlack, 16, 16, 16, 16);
            zGraphics.FillRectangle(zWhite, 0, 16, 16, 16);
            panelCardCanvas.BackgroundImage = zBitmap;
            CanvasUserAction = false;
            UpdateText();
        }

        public static MDICanvas Instance
        {
            get
            {
                if (null == s_zInstance)
                    s_zInstance = new MDICanvas();
                return s_zInstance;
            }
        }

        public CardCanvas SetupCardCanvas()
        {
            // m_zCardCanvas is a panel within the panelCardCanvas
            m_zCardCanvas = new CardCanvas
            {
                Location = new Point(0, 0),
                Size = new Size(0, 0)
            };
            m_zCardCanvas.MouseMove += cardCanvas_MouseMove;
            m_zCardCanvas.MouseDown += cardCanvas_MouseDown;
            m_zCardCanvas.MouseUp += cardCanvas_MouseUp;
            m_zCardCanvas.ContextMenuStrip = m_zContextMenu;
            m_zContextMenu.RenderMode = ToolStripRenderMode.System;
            m_zContextMenu.Opening += contextmenuOpening_Handler;

            panelCardCanvas.Controls.Add(m_zCardCanvas);
            return m_zCardCanvas;
        }


        protected override CreateParams CreateParams
        {
            get
            {
                const int CP_NOCLOSE_BUTTON = 0x200;
                CreateParams mdiCp = base.CreateParams;
                mdiCp.ClassStyle = mdiCp.ClassStyle | CP_NOCLOSE_BUTTON;
                return mdiCp;
            }
        }

        private void contextmenuOpening_Handler(object sender, CancelEventArgs e)
        {
            m_zContextMenu.Items.Clear();
            if (null != m_zCardCanvas.ActiveDeck.CardLayout.Element)
            {
                Point pointMouse = m_zCardCanvas.PointToClient(MousePosition);
                // add only the items that the mouse is within the rectangle of
                foreach (ProjectLayoutElement zElement in m_zCardCanvas.ActiveDeck.CardLayout.Element)
                {
                    if (!zElement.enabled)
                    {
                        continue;
                    }
                    var zRect = new RectangleF(zElement.x*m_fZoom, zElement.y*m_fZoom, zElement.width*m_fZoom,
                        zElement.height*m_fZoom);
                    if (zRect.Contains(pointMouse))
                    {
                        m_zContextMenu.Items.Add(zElement.name, null, contextmenuClick_Handler);
                    }
                }
            }
            if (1 == m_zContextMenu.Items.Count)
            {
                // auto-select if there's only one!
                contextmenuClick_Handler(m_zContextMenu.Items[0], new EventArgs());
                e.Cancel = true;
            }
            if (0 == m_zContextMenu.Items.Count)
            {
                e.Cancel = true;
            }
        }

        private void contextmenuClick_Handler(object sender, EventArgs e)
        {
            var zItem = (ToolStripItem) sender;
            MDILayoutControl.Instance.ChangeSelectedElement(zItem.Text);
        }

        private void cardCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (MouseButtons.Left == e.Button)
            {
                if (m_bElementSelected)
                {
                    int nX = e.X;
                    int nY = e.Y;
                    if (Cursor == Cursors.SizeAll)
                    {
                        if (TranslationLock.WaitingToSet == m_eTranslationLock)
                        {
                            // setup the lock (if outside the dead zone)
                            int nXDiff = Math.Abs(nX - m_pointOriginalMouseDown.X);
                            int nYDiff = Math.Abs(nY - m_pointOriginalMouseDown.Y);
                            if (nXDiff > TRANSLATION_LOCK_DEAD_ZONE || nYDiff > TRANSLATION_LOCK_DEAD_ZONE)
                            {
                                m_eTranslationLock = nXDiff > nYDiff
                                    ? TranslationLock.Horizontal
                                    : TranslationLock.Vertical;
                            }
                        }

                        switch (m_eTranslationLock)
                        {
                            case TranslationLock.Horizontal:
                                nY = m_pointOriginalMouseDown.Y;
                                break;
                            case TranslationLock.Vertical:
                                nX = m_pointOriginalMouseDown.X;
                                break;
                        }

                        var nXUnzoomed = (int) ((float) nX*m_fZoomRatio);
                        var nYUnzoomed = (int) ((float) nY*m_fZoomRatio);

                        if (null != m_listSelectedElements)
                        {
                            int idx = 0;
                            foreach (var selectedElement in m_listSelectedElements)
                            {
                                selectedElement.x = nXUnzoomed - m_listSelectedOffsets[idx].X;
                                selectedElement.y = nYUnzoomed - m_listSelectedOffsets[idx].Y;
                                idx++;
                            }
                        }

                        MDIElementControl.Instance.UpdateCurrentElementExtents();
                    }
                    else if (Cursor == Cursors.SizeNS)
                    {
                        switch (m_eResizeDirection)
                        {
                            case ResizeDirection.Up:
                                ResizeUp(nY);
                                break;
                            case ResizeDirection.Down:
                                ResizeDown(nY);
                                break;
                        }
                    }
                    else if (Cursor == Cursors.SizeWE)
                    {
                        switch (m_eResizeDirection)
                        {
                            case ResizeDirection.Left:
                                ResizeLeft(nX);
                                break;
                            case ResizeDirection.Right:
                                ResizeRight(nX);
                                break;
                        }
                    }
                    else if (Cursor == Cursors.SizeNESW)
                    {
                        switch (m_eResizeDirection)
                        {
                            case ResizeDirection.UpRight:
                                ResizeUp(nY);
                                ResizeRight(nX);
                                break;
                            case ResizeDirection.DownLeft:
                                ResizeDown(nY);
                                ResizeLeft(nX);
                                break;
                        }
                    }
                    else if (Cursor == Cursors.SizeNWSE)
                    {
                        switch (m_eResizeDirection)
                        {
                            case ResizeDirection.UpLeft:
                                ResizeUp(nY);
                                ResizeLeft(nX);
                                break;
                            case ResizeDirection.DownRight:
                                ResizeDown(nY);
                                ResizeRight(nX);
                                break;
                        }
                    }
                }
            }
            if (MouseButtons.None == e.Button)
            {
                // NOTE: this is evaluated every movement... might be a bit much
                var zElement = MDILayoutControl.Instance.GetSelectedLayoutElement();
                if (null != zElement)
                {
                    m_eResizeDirection = ResizeDirection.None;

                    var nX = e.X;
                    var nY = e.Y;

                    var nEdgeRight = (int) (zElement.x*m_fZoom + zElement.width*m_fZoom + SELECTION_BUFFER);
                    var nEdgeLeft = (int) (zElement.x*m_fZoom - SELECTION_BUFFER);
                    var nEdgeTop = (int) (zElement.y*m_fZoom - SELECTION_BUFFER);
                    var nEdgeBottom = (int) (zElement.y*m_fZoom + zElement.height*m_fZoom + SELECTION_BUFFER);

                    // first verify the cursor is within the SELECTION_BUFFER sized area
                    if ((nX >= (nEdgeLeft)) && (nX <= (nEdgeRight)) &&
                        (nY >= (nEdgeTop)) && (nY <= (nEdgeBottom)))
                    {

                        if (SELECTION_BUFFER_SPACE >= (nX - nEdgeLeft))
                        {
                            m_eResizeDirection |= ResizeDirection.Left;
                        }

                        if (SELECTION_BUFFER_SPACE >= (nEdgeRight - nX))
                        {
                            m_eResizeDirection |= ResizeDirection.Right;
                        }

                        if (SELECTION_BUFFER_SPACE >= (nY - nEdgeTop))
                        {
                            m_eResizeDirection |= ResizeDirection.Up;
                        }

                        if (SELECTION_BUFFER_SPACE >= (nEdgeBottom - nY))
                        {
                            m_eResizeDirection |= ResizeDirection.Down;
                        }

                        if (MouseMode.Move == m_eMouseMode)
                            m_eResizeDirection = ResizeDirection.None;

                        switch (m_eResizeDirection)
                        {
                            case ResizeDirection.Down:
                            case ResizeDirection.Up:
                                Cursor = Cursors.SizeNS;
                                break;
                            case ResizeDirection.Left:
                            case ResizeDirection.Right:
                                Cursor = Cursors.SizeWE;
                                break;
                            case ResizeDirection.UpLeft:
                            case ResizeDirection.DownRight:
                                Cursor = Cursors.SizeNWSE;
                                break;
                            case ResizeDirection.UpRight:
                            case ResizeDirection.DownLeft:
                                Cursor = Cursors.SizeNESW;
                                break;
                            case ResizeDirection.None:
                                // if no edge was found to be selected it's a move
                                Cursor = Cursors.SizeAll;
                                m_eResizeDirection = ResizeDirection.Move;
                                break;

                        }
                    }
                    else
                    {
                        Cursor = Cursors.Default;
                    }
                }
            }
        }

        public void ResizeRight(int nX)
        {
            int nWidth = (int) ((float) nX*m_fZoomRatio) - MDIElementControl.Instance.ElementX;
            if (1 <= nWidth)
            {
                MDIElementControl.Instance.ElementW = nWidth;
                m_zSelectedElement.width = nWidth;
            }
        }

        public void ResizeLeft(int nX)
        {
            int nWidth = m_zSelectedElement.width + (m_zSelectedElement.x - (int) ((float) nX*m_fZoomRatio));
            if ((0 <= nX) && (1 <= nWidth))
            {
                m_zSelectedElement.width = nWidth;
                m_zSelectedElement.x = (int) ((float) nX*m_fZoomRatio);
                MDIElementControl.Instance.ElementW = nWidth;
                MDIElementControl.Instance.ElementX = m_zSelectedElement.x;
            }
        }

        public void ResizeUp(int nY)
        {
            int nHeight = m_zSelectedElement.height + (m_zSelectedElement.y - (int) ((float) nY*m_fZoomRatio));
            if ((0 <= nY) && (1 <= nHeight))
            {
                m_zSelectedElement.height = nHeight;
                m_zSelectedElement.y = (int) ((float) nY*m_fZoomRatio);
                MDIElementControl.Instance.ElementH = nHeight;
                MDIElementControl.Instance.ElementY = m_zSelectedElement.y;
            }
        }

        public void ResizeDown(int nY)
        {
            int nHeight = (int) ((float) nY*m_fZoomRatio) - MDIElementControl.Instance.ElementY;
            if (1 <= nHeight)
            {
                m_zSelectedElement.height = nHeight;
                MDIElementControl.Instance.ElementH = nHeight;
            }
        }

        private void cardCanvas_MouseUp(object sender, MouseEventArgs e)
        {
            CanvasUserAction = false;
            if (null != m_listSelectedElements && m_bElementSelected)
            {
                ConfigureUserAction(m_dictionarySelectedUndo, GetUndoRedoPoints());
            }

            m_dictionarySelectedUndo = null;
            m_listSelectedElements = null;
            m_zSelectedElement = null;
            m_bElementSelected = false;
        }

        private void ConfigureUserAction(Dictionary<ProjectLayoutElement, Rectangle> dictionarySelectedUndo,
            Dictionary<ProjectLayoutElement, Rectangle> dictionarySelectedRedo)
        {
#warning check on closure needs for this... 
            // configure the variables used for undo/redo
            var dictionaryUndoElements = dictionarySelectedUndo;
            var dictionaryRedoElements = dictionarySelectedRedo;

            UserAction.PushAction(bRedo =>
            {
                Dictionary<ProjectLayoutElement, Rectangle> dictionaryElementsChange = bRedo
                    ? dictionaryRedoElements
                    : dictionaryUndoElements;
                foreach (var kvp in dictionaryElementsChange)
                {
                    Rectangle rectChange = kvp.Value;
                    ProjectLayoutElement zElement = kvp.Key;
                    zElement.x = rectChange.X;
                    zElement.y = rectChange.Y;
                    zElement.width = rectChange.Width;
                    zElement.height = rectChange.Height;
                }
                MDIElementControl.Instance.UpdateCurrentElementExtents();
            });
        }

        private void cardCanvas_MouseDown(object sender, MouseEventArgs e)
        {
            if (MouseButtons.Left == e.Button)
            {
                ProjectLayoutElement zElement = MDILayoutControl.Instance.GetSelectedLayoutElement();
                if (null != zElement)
                {
                    CanvasUserAction = true;

                    m_pointOriginalMouseDown = e.Location;
                    if (TranslationLock.WaitingToSet != m_eTranslationLock)
                    {
                        m_eTranslationLock = TranslationLock.Unset;
                    }

                    int nX = e.X;
                    int nY = e.Y;

                    if ((nX >= (int) (zElement.x*m_fZoom - SELECTION_BUFFER) &&
                         nX <= (int) (zElement.x*m_fZoom + zElement.width*m_fZoom + SELECTION_BUFFER)) &&
                        (nY >= (int) (zElement.y*m_fZoom - SELECTION_BUFFER) &&
                         nY <= (int) (zElement.y*m_fZoom + zElement.height*m_fZoom + SELECTION_BUFFER)))
                    {
                        // Setup the start position and allow movement
                        var nXUnzoomed = (int) ((float) nX*m_fZoomRatio);
                        var nYUnzoomed = (int) ((float) nY*m_fZoomRatio);
                        // store the offset in "normal" size
                        m_bElementSelected = true;
                        m_zSelectedElement = zElement;

                        m_listSelectedElements = MDILayoutControl.Instance.GetSelectedLayoutElements();
                        if (null != m_listSelectedElements)
                        {
                            m_listSelectedOffsets = new List<Point>();
                            foreach (var zSelectedElement in m_listSelectedElements)
                            {
                                m_listSelectedOffsets.Add(new Point(
                                    nXUnzoomed - zSelectedElement.x,
                                    nYUnzoomed - zSelectedElement.y));
                            }
                            // setup the undo dictionary (covers all types of changes allowed with canvas mouse movement)
                            m_dictionarySelectedUndo = GetUndoRedoPoints();
                        }
                    }
                }
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (!numericUpDownZoom.Focused)
            {
                int nHChange = 0;
                int nVChange = 0;
                // NOTE: this method only detects keydown events
                switch (keyData)
                {
                    case Keys.Control | Keys.Add:
                        numericUpDownZoom.Value = Math.Min(numericUpDownZoom.Maximum,
                            numericUpDownZoom.Value + numericUpDownZoom.Increment);
                        break;
                    case Keys.Control | Keys.Subtract:
                        numericUpDownZoom.Value = Math.Max(numericUpDownZoom.Minimum,
                            numericUpDownZoom.Value - numericUpDownZoom.Increment);
                        break;
                        // focus is taken by the MDICanvas, reset it after each change below & reset the translation lock
                    case Keys.Shift | Keys.Up:
                        MDILayoutControl.Instance.ChangeElementOrder(-1);
                        m_eTranslationLock = TranslationLock.Unset;
                        m_zCardCanvas.Focus();
                        break;
                    case Keys.Shift | Keys.Down:
                        MDILayoutControl.Instance.ChangeElementOrder(1);
                        m_eTranslationLock = TranslationLock.Unset;
                        m_zCardCanvas.Focus();
                        break;
                    case Keys.Control | Keys.Up:
                        MDILayoutControl.Instance.ChangeSelectedElement(-1);
                        m_eTranslationLock = TranslationLock.Unset;
                        m_zCardCanvas.Focus();
                        break;
                    case Keys.Control | Keys.Down:
                        MDILayoutControl.Instance.ChangeSelectedElement(1);
                        m_eTranslationLock = TranslationLock.Unset;
                        m_zCardCanvas.Focus();
                        break;
                    case Keys.ShiftKey | Keys.Shift:
                        if (TranslationLock.Unset == m_eTranslationLock)
                        {
                            m_eTranslationLock = TranslationLock.WaitingToSet;
                        }
                        break;
                    case Keys.Up:
                        nVChange = -1;
                        m_zCardCanvas.Focus();
                        break;
                    case Keys.Down:
                        nVChange = 1;
                        m_zCardCanvas.Focus();
                        break;
                    case Keys.Left:
                        nHChange = -1;
                        m_zCardCanvas.Focus();
                        break;
                    case Keys.Right:
                        nHChange = 1;
                        m_zCardCanvas.Focus();
                        break;
                    case Keys.M:
                        m_eMouseMode = MouseMode.Move == m_eMouseMode
                            ? MouseMode.MoveResize
                            : MouseMode.Move;
                        UpdateText();
                        // get the position of the mouse to trigger a standard mouse move (updates the cursor/mode)
                        var pLocation = panelCardCanvas.PointToClient(Cursor.Position);
                        cardCanvas_MouseMove(null, new MouseEventArgs(MouseButtons.None, 0, pLocation.X, pLocation.Y, 0));
                        break;

                }
                ProcessSelectedElementsChange(nHChange, nVChange, 0, 0);
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        public void ProcessSelectedElementsChange(int nX, int nY, int nWidth, int nHeight, decimal dScaleWidth = 1, decimal dScaleHeight = 1)
        {
            if (nX == 0 && nY == 0 && nWidth == 0 && nHeight == 0 && dScaleWidth == 1 && dScaleHeight == 1)
            {
                return;
            }
            m_listSelectedElements = MDILayoutControl.Instance.GetSelectedLayoutElements();

            if (null != m_listSelectedElements)
            {

                Dictionary<ProjectLayoutElement, Rectangle> dictionarySelectedUndo = GetUndoRedoPoints();

                if (dScaleWidth != 1 || dScaleHeight != 1)
                {
                    foreach (var zElement in m_listSelectedElements)
                    {
                        zElement.width = (int)Math.Max(1, zElement.width * dScaleWidth);
                        zElement.height = (int)Math.Max(1, zElement.height * dScaleHeight);
                    }
                }
                else
                {
                    foreach (var zElement in m_listSelectedElements)
                    {
                        zElement.x = Math.Max(1, zElement.x + nX);
                        zElement.y = Math.Max(1, zElement.y + nY);
                        zElement.width = Math.Max(1, zElement.width + nWidth);
                        zElement.height = Math.Max(1, zElement.height + nHeight);
                    }
                }

                ConfigureUserAction(dictionarySelectedUndo, GetUndoRedoPoints());

                MDIElementControl.Instance.UpdateCurrentElementExtents();
                m_listSelectedElements = null;
            }           
        }

        private void numericUpDownZoom_ValueChanged(object sender, EventArgs e)
        {
            m_fZoom = (float)numericUpDownZoom.Value;
            m_fZoomRatio = 1.0f / m_fZoom;
            m_eTranslationLock = TranslationLock.Unset;
            m_zCardCanvas.CardRenderer.ZoomLevel = m_fZoom;
            m_zCardCanvas.ActiveDeck.ResetDeckCache();
            m_zCardCanvas.UpdateSize();
            m_zCardCanvas.Invalidate();
        }

        private void UpdateText()
        {
            Text = MouseMode.MoveResize == m_eMouseMode ? "Canvas [Mode: Normal]" : "Canvas [Mode: Move-only]";
        }

        private Dictionary<ProjectLayoutElement, Rectangle> GetUndoRedoPoints()
        {
            if (null != m_listSelectedElements)
            {
                var dictionarySelectedUndo = new Dictionary<ProjectLayoutElement, Rectangle>();
                foreach (var zElement in m_listSelectedElements)
                {
                    dictionarySelectedUndo.Add(zElement, new Rectangle(zElement.x, zElement.y, zElement.width, zElement.height));
                }
                return dictionarySelectedUndo;
            }
            return null;
        }
    }
}