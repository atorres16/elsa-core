import {Component, h, Host, Prop} from '@stencil/core';
import {eventBus} from "../../../services";
import {EventTypes} from "../../../models";

@Component({
  tag: 'elsa-copy-button',
  shadow: false,
})
export class ElsaCopyButton {
  @Prop() value: string;

  render() {
    return (
      <Host class="elsa-align-text-top elsa-inline-block">
        <button type="button" class="hover:elsa-text-blue-500 focus:elsa-text-green-600 elsa-pl-0.5" onClick={this.onCopyClick}>
          <svg xmlns="http://www.w3.org/2000/svg" class="elsa-h-4 elsa-w-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M8 5H6a2 2 0 00-2 2v12a2 2 0 002 2h10a2 2 0 002-2v-1M8 5a2 2 0 002 2h2a2 2 0 002-2M8 5a2 2 0 012-2h2a2 2 0 012 2m0 0h2a2 2 0 012 2v3m2 4H10m0 0l3-3m-3 3l3 3" />
          </svg>
        </button>
      </Host>
    );
  }

  onCopyClick = async (e) => {
    e.stopPropagation();

    this.checkClipboardPermissions();

    await navigator.clipboard.writeText(this.value);
    await eventBus.emit(EventTypes.ClipboardCopied, this, undefined, 'Data copied to clipboard.');
  }

  checkClipboardPermissions = () => {
    navigator.permissions.query({name: 'clipboard-read'}).then((result) => {
      if (result.state == 'denied')
        eventBus.emit(EventTypes.ClipboardPermissionDenied, this);
    });
  }

}
