; Identify the modified widget itself; descriptor IDs can change on a rebuild.
reserve locals
push_null
load_slot slot:id
load_slot slot:this
get_member member:navigation/NavigationPanelController::activeBtns
get_index_or_default
store_slot slot:buttonController
pop
load_slot slot:buttonController
push_null
op_48 7
jump_if_false_pop inspect
jump release
inspect:
load_slot slot:buttonController
get_member member:button/NavButtonController::desc
get_member member:navigation/NavButtonDesc::uiInputName
push_constant constant:empty
op_48 7
jump_if_false_pop release
reserve 1
load_slot slot:buttonController
push_int 0
call member:component/ComponentUiControllerBase::GetView
store_slot slot:view
pop
reserve 1
load_slot slot:view
push_int 0
call member:buttonView/NavButtonView::GetUiObject
store_slot slot:buttonObject
pop
load_slot slot:buttonObject
get_member member:legend/UiNavigationLegendNavigationpanelbutton::tfCont
store_slot slot:textContainer
pop
load_slot slot:textContainer
get_member member:legend/UiNavigationLegendNavbtntf::textField
store_slot slot:label
pop
load_slot slot:textContainer
get_member member:graphics/UiDisplayObjectBase::x
push_constant constant:zero
op_48 7
jump_if_false_pop release
load_slot slot:textContainer
get_member member:graphics/UiDisplayObjectBase::y
push_constant constant:zero
op_48 7
jump_if_false_pop release
load_slot slot:label
get_member member:graphics/UiDisplayObjectBase::scaleX
push_constant constant:indicatorScale
op_48 7
jump_if_false_pop release
load_slot slot:label
get_member member:graphics/UiDisplayObjectBase::scaleY
push_constant constant:indicatorScale
op_48 7
jump_if_false_pop release

; Restore the stock transforms before returning the widget to the shared pool.
push_constant constant:zero
load_slot slot:buttonObject
set_member member:graphics/UiDisplayObjectBase::x
pop
push_constant constant:zero
load_slot slot:buttonObject
set_member member:graphics/UiDisplayObjectBase::y
pop
push_constant constant:originalTextOffset
load_slot slot:textContainer
set_member member:graphics/UiDisplayObjectBase::x
pop
push_constant constant:zero
load_slot slot:textContainer
set_member member:graphics/UiDisplayObjectBase::y
pop
push_constant constant:originalTextOpacity
load_slot slot:textContainer
set_member member:graphics/UiDisplayObjectBase::alpha
pop
push_constant constant:zero
load_slot slot:label
set_member member:graphics/UiDisplayObjectBase::x
pop
push_constant constant:normalScale
load_slot slot:label
set_member member:graphics/UiDisplayObjectBase::scaleX
pop
push_constant constant:normalScale
load_slot slot:label
set_member member:graphics/UiDisplayObjectBase::scaleY
pop
push_constant constant:normalScale
load_slot slot:label
set_member member:graphics/UiDisplayObjectBase::alpha
pop
load_slot slot:view
get_member member:buttonView/NavButtonView::textInitialY
load_slot slot:label
set_member member:graphics/UiDisplayObjectBase::y
pop
; Restore only the container animation; the label keeps its native flags.
push_bool true
load_slot slot:textContainer
set_member member:graphics/UiDisplayObjectBase::animated
pop
release:
reserve 1
load_slot slot:id
load_slot slot:this
push_int 1
push_constant constant:basePanel
call_type_member member:navigation/NavigationPanelController::RemoveBtn
pop
return arguments
