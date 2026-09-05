import { render } from '@solidjs/web'
import './styles.css'
import { App } from './components/App'

const root = document.getElementById('root')
if (!root) throw new Error('missing #root')
render(() => <App />, root)
