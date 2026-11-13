; IsButtonHidden
load_slot slot:buttonId
push_constant constant:buttonId
load_slot slot:this
get_member member:NavigationPanelController::btns
get_index
get_member member:navigation/NavButtonDesc::id
op_48 7
return_value arguments
