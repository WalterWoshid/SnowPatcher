; PlaceButtons

; Keep the base layout, then find the reused button.
reserve locals
reserve 1
load_slot slot:this
push_int 0
push_constant constant:basePanel
call_type_member member:navigation/NavigationPanelController::PlaceButtons
pop
push_null
push_constant constant:buttonId
load_slot slot:this
get_member member:NavigationPanelController::btns
get_index
get_member member:navigation/NavButtonDesc::id
load_slot slot:this
get_member member:navigation/NavigationPanelController::activeBtns
get_index_or_default
store_slot slot:buttonController
pop
load_slot slot:buttonController
push_null
op_48 7
jump_if_false_pop at23
return arguments
at23:
reserve 1
reserve 1
load_slot slot:buttonController
push_int 0

; Read its display object.
call member:component/ComponentUiControllerBase::GetView
push_int 0
call member:buttonView/NavButtonView::GetUiObject
store_slot slot:buttonObject
pop
; The stock clip places its text container at (40, 0). Refuse other layouts.
load_slot slot:buttonObject
get_member member:legend/UiNavigationLegendNavigationpanelbutton::tfCont
store_slot slot:textContainer
pop
load_slot slot:textContainer
get_member member:legend/UiNavigationLegendNavbtntf::textField
store_slot slot:label
pop
load_slot slot:textContainer
get_member member:graphics/UiDisplayObjectBase::y
push_constant constant:zero
op_48 7
jump_if_false_pop unsupported
load_slot slot:textContainer
get_member member:graphics/UiDisplayObjectBase::x
push_constant constant:originalTextOffset
op_48 7
jump_if_false_pop checkModified
load_slot slot:label
get_member member:graphics/UiDisplayObjectBase::scaleX
push_constant constant:opacity
op_48 7
jump_if_false_pop unsupported
load_slot slot:label
get_member member:graphics/UiDisplayObjectBase::scaleY
push_constant constant:opacity
op_48 7
jump_if_false_pop unsupported
load_slot slot:textContainer
get_member member:graphics/UiDisplayObjectBase::animated
jump_if_false_pop unsupported
jump ready
checkModified:
load_slot slot:textContainer
get_member member:graphics/UiDisplayObjectBase::x
push_constant constant:zero
op_48 7
jump_if_false_pop unsupported
load_slot slot:label
get_member member:graphics/UiDisplayObjectBase::scaleX
push_constant constant:scale
op_48 7
jump_if_false_pop unsupported
load_slot slot:label
get_member member:graphics/UiDisplayObjectBase::scaleY
push_constant constant:scale
op_48 7
jump_if_false_pop unsupported
jump ready
unsupported:
push_bool false
load_slot slot:buttonObject
set_member member:graphics/UiDisplayObjectBase::visible
pop
return arguments
ready:
reserve 1
reserve 1

; Move it to the main-menu layer.
push_int 9
reserve 1
load_slot slot:this
get_member member:component/ComponentUiControllerBase::uiSystem
push_int 0
call member:uiSystem/UiSystem::GetLayersProvider
push_int 1
call member:layers/LayersProvider::GetLayer
load_slot slot:buttonObject
push_int 1
call member:graphics/UiDisplayObjectBase::SetParent
pop
push_constant constant:zero
load_slot slot:buttonObject
set_member member:graphics/UiDisplayObjectBase::x
pop
push_constant constant:zero
load_slot slot:buttonObject
set_member member:graphics/UiDisplayObjectBase::y
pop
load_slot slot:buttonObject
get_member member:legend/UiNavigationLegendNavigationpanelbutton::tfCont
store_slot slot:textContainer
pop
push_constant constant:zero
load_slot slot:textContainer
set_member member:graphics/UiDisplayObjectBase::x
pop
push_constant constant:zero
load_slot slot:textContainer
set_member member:graphics/UiDisplayObjectBase::y
pop
; The native text container animates its alpha back to 50 percent.
push_bool false
load_slot slot:textContainer
set_member member:graphics/UiDisplayObjectBase::animated
pop
push_constant constant:opacity
load_slot slot:textContainer
set_member member:graphics/UiDisplayObjectBase::alpha
pop
load_slot slot:textContainer
get_member member:legend/UiNavigationLegendNavbtntf::textField
store_slot slot:label
pop

; Scale the label to 150 percent.
push_constant constant:scale
load_slot slot:label
set_member member:graphics/UiDisplayObjectBase::scaleX
pop
push_constant constant:scale
load_slot slot:label
set_member member:graphics/UiDisplayObjectBase::scaleY
pop
push_constant constant:zero
load_slot slot:label
set_member member:graphics/UiDisplayObjectBase::x
pop
push_constant constant:zero
load_slot slot:label
set_member member:graphics/UiDisplayObjectBase::y
pop
push_constant constant:opacity
load_slot slot:label
set_member member:graphics/UiDisplayObjectBase::alpha
pop
reserve 1
load_slot slot:this
get_member member:component/ComponentUiControllerBase::uiSystem
get_member member:uiSystem/UiSystem::uiRoot
push_int 0

; Use the native viewport to find the bottom-left corner.
call member:graphics/UiMovieBase::GetViewportBottomRight
store_slot slot:corner
pop
push_constant constant:margin
load_slot slot:corner
set_member member:graphics/Vector2d::x
pop
load_slot slot:corner
get_member member:graphics/Vector2d::y
push_constant constant:margin
subtract_float
load_slot slot:corner
set_member member:graphics/Vector2d::y
pop
reserve 1
load_slot slot:corner
load_slot slot:textContainer
push_int 1
call member:graphics/UiDisplayObjectBase::GlobalToLocal
store_slot slot:corner
pop
load_slot slot:corner
get_member member:graphics/Vector2d::x
load_slot slot:label
set_member member:graphics/UiDisplayObjectBase::x
pop
load_slot slot:corner
get_member member:graphics/Vector2d::y
load_slot slot:label
get_member member:graphics/UiLabel::textHeight
push_constant constant:scaleFactor
multiply_float
subtract_float
load_slot slot:label
set_member member:graphics/UiDisplayObjectBase::y
pop
return arguments
